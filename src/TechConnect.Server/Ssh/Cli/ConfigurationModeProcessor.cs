// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConfigurationModeProcessor.cs
//
// Purpose:
// Parses and executes Cisco-style configuration-mode lines against an
// operator's private candidate configuration, and provides Tab / ? discovery.
//
// -----------------------------------------------------------------------------

using System.Text;
using System.Text.Json.Nodes;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>What the CLI session must do after a configuration-mode line.</summary>
internal enum ConfigurationLineAction
{
    /// <summary>Stay in configuration mode.</summary>
    Continue,

    /// <summary>Leave configuration mode (discarding unsaved changes).</summary>
    Leave,

    /// <summary>Read a secret with hidden input, then call <see cref="ConfigurationModeProcessor.ApplySecret"/>.</summary>
    PromptSecret,

    /// <summary>Persist the candidate (<c>write</c>).</summary>
    Write,

    /// <summary>
    /// Run <see cref="ConfigurationLineResult.ExecCommand"/> (a read-only <c>show</c> command from <c>do</c>)
    /// through the privileged EXEC dispatcher, which authorizes it.
    /// </summary>
    ExecuteExec
}

/// <summary>
/// Pending hidden-input secret assignment.
/// </summary>
/// <param name="Setting">Secret setting.</param>
/// <param name="Container">Candidate object that owns the setting.</param>
/// <param name="Subject">Hash subject (username for passwords).</param>
/// <param name="AddEntry">
/// When set, <paramref name="Container"/> is a new entry that this adds to the candidate only after the
/// secret is accepted, so a cancelled or rejected prompt leaves no half-created entry.
/// </param>
/// <param name="EntryName">
/// Display name of the entry, for example <c>enable-secret-level 5</c>: named in creation messages and, for an
/// existing one-line entry, in the hint for removing it.
/// </param>
internal sealed record ConfigurationSecretRequest(
    ConfigSetting Setting,
    JsonObject Container,
    string Subject,
    Action? AddEntry = null,
    string? EntryName = null)
{
    /// <summary>First prompt text.</summary>
    public string Prompt => Setting.Kind == ConfigValueKind.PasswordHash ? "Enter password: " : "Enter secret: ";

    /// <summary>Confirmation prompt text.</summary>
    public string ConfirmPrompt => Setting.Kind == ConfigValueKind.PasswordHash ? "Confirm password: " : "Confirm secret: ";

    /// <summary>Printed when either prompt is cancelled.</summary>
    public string CancelledMessage => AddEntry is null
        ? "% Cancelled. Value not changed."
        : "% Cancelled. '" + EntryName + "' was not created.";
}

/// <summary>
/// Result of one configuration-mode line.
/// </summary>
/// <param name="Action">Follow-up action.</param>
/// <param name="Output">Text to print (no trailing newline).</param>
/// <param name="Secret">Secret request for <see cref="ConfigurationLineAction.PromptSecret"/>.</param>
/// <param name="ForgetHistory">The line may contain secret material and must be dropped from history.</param>
/// <param name="ExecCommand">EXEC command line for <see cref="ConfigurationLineAction.ExecuteExec"/>.</param>
internal sealed record ConfigurationLineResult(
    ConfigurationLineAction Action,
    string? Output = null,
    ConfigurationSecretRequest? Secret = null,
    bool ForgetHistory = false,
    string? ExecCommand = null);

/// <summary>
/// Configuration-mode command interpreter (stateless; state lives in <see cref="ConfigurationEditSession"/>).
/// </summary>
/// <remarks>
/// Keywords accept unique prefixes (an exact match wins). Every line is re-authorized as
/// <see cref="CliCommandId.ConfigurationCommand"/> (privilege 15). Secrets are never echoed:
/// they are entered at a hidden prompt (or as an existing hash) and displayed as <c>&lt;hidden&gt;</c>.
/// </remarks>
internal sealed class ConfigurationModeProcessor
{
    /// <summary>Warning printed when leaving with unsaved changes.</summary>
    public const string DiscardedMessage = "% Unsaved configuration changes discarded.";

    /// <summary>Printed on entry.</summary>
    public const string EntryMessage = "Enter configuration commands, one per line.  End with 'end'.";

    /// <summary>Incomplete-command error.</summary>
    private const string Incomplete = "% Incomplete command.";

    /// <summary>Plaintext-secret rejection.</summary>
    private const string PlaintextRejected =
        "% Invalid input detected: secrets are not accepted on the command line. Enter the keyword alone to be prompted";

    /// <summary>Configuration management (views, validation).</summary>
    private readonly ConfigurationManagementService _management;

    /// <summary>Per-line authorization.</summary>
    private readonly OperatorAuthorizationService _authorization;

    /// <summary>Identity V3 hasher.</summary>
    private readonly OperatorPasswordHasher _hasher = new();

    /// <summary>Creates the processor.</summary>
    /// <param name="management">Configuration management service.</param>
    /// <param name="authorization">Authorization service.</param>
    public ConfigurationModeProcessor(ConfigurationManagementService management, OperatorAuthorizationService authorization)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    /// <summary>Kinds of configuration-mode keywords.</summary>
    private enum EntryKind
    {
        Exit,
        End,
        Write,
        Show,
        Validate,
        Help,
        No,
        Default,
        Do,
        Setting,
        Section,
        Collection
    }

    /// <summary>A keyword available in the current context.</summary>
    private sealed record Entry(string Keyword, string Description, EntryKind Kind, ConfigSetting? Setting = null, ConfigSection? Section = null, ConfigCollection? Collection = null);

    /// <summary>A token and its offset in the input line.</summary>
    private readonly record struct Token(string Text, int Start);

    /// <summary>Keyword resolution outcome.</summary>
    private enum MatchOutcome
    {
        None,
        Unique,
        Ambiguous
    }

    /// <summary>Configuration-mode prompt, for example <c>TechConnect(config-ssh)#</c>.</summary>
    public static string GetPrompt(ConfigurationEditSession edit) => "TechConnect(" + edit.Mode + ")#";

    /// <summary>
    /// Executes one configuration-mode line.
    /// </summary>
    /// <param name="edit">Operator edit session.</param>
    /// <param name="identity">Operator identity with active privilege.</param>
    /// <param name="input">Raw line.</param>
    /// <returns>Result for the session loop.</returns>
    public ConfigurationLineResult Execute(ConfigurationEditSession edit, AuthenticatedOperator identity, string? input)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!_authorization.TryAuthorize(identity, CliCommandId.ConfigurationCommand, out _))
        {
            return new ConfigurationLineResult(ConfigurationLineAction.Leave, OperatorAuthorizationService.PermissionDeniedMessage);
        }

        var line = input ?? string.Empty;
        var tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            return new ConfigurationLineResult(ConfigurationLineAction.Continue);
        }

        var prompt = GetPrompt(edit);
        var entries = EntriesFor(edit.Current, includeGlobals: true);
        var (outcome, entry) = Resolve(entries, tokens[0].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return Error(outcome, prompt, line, tokens[0]);
        }

        switch (entry!.Kind)
        {
            case EntryKind.Exit:
                return NoArguments(tokens, prompt, line) ??
                       (edit.Pop()
                           ? new ConfigurationLineResult(ConfigurationLineAction.Continue)
                           : new ConfigurationLineResult(ConfigurationLineAction.Leave));
            case EntryKind.End:
                return NoArguments(tokens, prompt, line) ?? new ConfigurationLineResult(ConfigurationLineAction.Leave);
            case EntryKind.Write:
                if (tokens.Count >= 2 && !"memory".StartsWith(tokens[1].Text, StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid(prompt, line, tokens[1]);
                }

                if (tokens.Count > 2)
                {
                    return Invalid(prompt, line, tokens[2]);
                }

                return new ConfigurationLineResult(ConfigurationLineAction.Write);
            case EntryKind.Help:
                return Continue(CliDiscoveryFormatter.FormatHelpListing(
                    entries.Select(e => (Syntax(e), e.Description)).ToList(),
                    CliDiscoveryFormatter.DefaultColumns));
            case EntryKind.Validate:
                return NoArguments(tokens, prompt, line) ?? Continue(FormatValidation(edit));
            case EntryKind.Show:
                return ExecuteShow(edit, tokens, prompt, line);
            case EntryKind.Do:
                return ExecuteDo(edit, tokens, prompt, line);
            case EntryKind.No:
            case EntryKind.Default:
                return ExecuteNegation(edit, entry.Kind == EntryKind.Default, line, tokens, prompt);
            case EntryKind.Section:
                return NoArguments(tokens, prompt, line) ?? EnterSection(edit, entry.Section!);
            case EntryKind.Collection when IsSingleLineCollection(entry.Collection!):
                return ExecuteSecretEntry(edit, entry.Collection!, line, tokens, prompt);
            case EntryKind.Collection:
                return ExecuteUsername(edit, entry.Collection!, line, tokens, prompt);
            default:
                return ExecuteSetting(edit, entry.Setting!, line, tokens, prompt);
        }
    }

    /// <summary>
    /// Completes a hidden-input secret assignment.
    /// </summary>
    /// <param name="request">Pending request.</param>
    /// <param name="secret">First entry.</param>
    /// <param name="confirmation">Confirmation entry.</param>
    /// <returns>Error text, or <see langword="null"/> when the candidate was updated.</returns>
    public string? ApplySecret(ConfigurationSecretRequest request, string secret, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unchanged = request.AddEntry is null ? " Value not changed." : " '" + request.EntryName + "' was not created.";
        if (!string.Equals(secret, confirmation, StringComparison.Ordinal))
        {
            return "% Entries do not match." + unchanged;
        }

        if (string.IsNullOrEmpty(secret))
        {
            return request.AddEntry is null
                ? "% An empty value is not accepted. Use 'no " + (request.EntryName ?? request.Setting.Keyword) + "' to remove it."
                : "% An empty value is not accepted." + unchanged;
        }

        if (secret.Length > request.Setting.MaxLength || secret.Any(char.IsControl))
        {
            return "% Invalid value." + unchanged;
        }

        var stored = request.Setting.Kind switch
        {
            ConfigValueKind.PasswordHash => _hasher.HashPassword(request.Subject, secret),
            ConfigValueKind.EnableSecretHash => _hasher.HashEnableSecret(secret),
            _ => secret
        };
        ConfigurationModel.SetValue(request.Container, request.Setting, JsonValue.Create(stored));
        request.AddEntry?.Invoke();
        return null;
    }

    /// <summary>Formats a write result in Cisco style.</summary>
    /// <param name="result">Write result.</param>
    /// <returns>Multi-line text.</returns>
    public static string FormatWriteResult(ConfigurationWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder("Building configuration...");
        if (result.Status != ConfigurationWriteStatus.Saved)
        {
            foreach (var message in result.Messages)
            {
                sb.Append("\r\n").Append(message);
            }

            return sb.ToString();
        }

        sb.Append("\r\n[OK]\r\nConfiguration saved.");
        foreach (var message in result.Messages)
        {
            sb.Append("\r\n").Append(message);
        }

        if (result.AppliedAtRuntime.Count > 0)
        {
            sb.Append("\r\nApplied to the running server: ").Append(string.Join(", ", result.AppliedAtRuntime));
        }

        if (result.RuntimeApplyFailures.Count > 0)
        {
            sb.Append("\r\n% Saved but not applied at runtime (takes effect after restart): ")
                .Append(string.Join(", ", result.RuntimeApplyFailures));
        }

        if (result.RestartRequired.Count > 0)
        {
            sb.Append("\r\n% Server restart required for: ").Append(string.Join(", ", result.RestartRequired));
            sb.Append("\r\n% The server was not restarted; restart it to activate these settings.");
        }

        return sb.ToString();
    }

    /// <summary>Tab completion in configuration mode.</summary>
    public CliDiscoveryResult Complete(ConfigurationEditSession edit, string line, int cursor, int terminalColumns)
    {
        var (start, partial, candidates) = Candidates(edit, line, cursor);
        var matches = candidates
            .Where(c => !c.Token.StartsWith('<') && c.Token.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Token)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        if (matches.Count == 1)
        {
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.UniqueCompletion,
                CompletionText = matches[0],
                AppendTrailingSpace = true,
                ReplaceStart = start,
                ReplaceEnd = cursor,
                Tokens = matches
            };
        }

        var exact = matches.FirstOrDefault(m => m.Equals(partial, StringComparison.OrdinalIgnoreCase));
        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.AmbiguousCompletion,
            ReplaceStart = start,
            ReplaceEnd = cursor,
            Tokens = matches,
            CompletionText = exact ?? LongestCommonPrefix(matches),
            DisplayText = CliDiscoveryFormatter.FormatCandidateColumns(matches, terminalColumns)
        };
    }

    /// <summary><c>?</c> context help in configuration mode.</summary>
    public CliDiscoveryResult Help(ConfigurationEditSession edit, string line, int cursor, int terminalColumns)
    {
        var (start, partial, candidates) = Candidates(edit, line, cursor);
        // A partly typed word must be completed before the line can execute, so <cr> is shown only after a space.
        var entries = candidates
            .Where(c => partial.Length == 0 ||
                        c.Token.StartsWith(partial, StringComparison.OrdinalIgnoreCase) ||
                        (c.Token.StartsWith('<') && c.Token != "<cr>"))
            .ToList();
        if (entries.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.ContextHelp,
            ReplaceStart = start,
            ReplaceEnd = cursor,
            Tokens = entries.Select(e => e.Token).ToList(),
            Descriptions = entries.Select(e => e.Description).ToList(),
            DisplayText = CliDiscoveryFormatter.FormatHelpListing(entries, terminalColumns)
        };
    }

    /// <summary>Discovery candidates for the token at <paramref name="cursor"/>.</summary>
    private static (int Start, string Partial, List<(string Token, string Description)> Candidates) Candidates(
        ConfigurationEditSession edit,
        string line,
        int cursor)
    {
        cursor = Math.Clamp(cursor, 0, line.Length);
        var before = line[..cursor];
        var tokens = Tokenize(before);
        var completingNew = before.Length == 0 || char.IsWhiteSpace(before[^1]);
        var partial = completingNew || tokens.Count == 0 ? string.Empty : tokens[^1].Text;
        var start = completingNew ? cursor : tokens[^1].Start;
        var preceding = completingNew ? tokens : tokens.Take(tokens.Count - 1).ToList();
        var list = new List<(string, string)>();
        var entries = EntriesFor(edit.Current, includeGlobals: true);

        if (preceding.Count == 0)
        {
            list.AddRange(entries.Select(e => (e.Keyword, e.Description)));
            return (start, partial, list);
        }

        var (outcome, first) = Resolve(entries, preceding[0].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return (start, partial, list);
        }

        var rest = preceding.Skip(1).ToList();
        switch (first!.Kind)
        {
            case EntryKind.Show:
                list.AddRange(ShowCandidates(rest));
                break;
            case EntryKind.Do:
                list.AddRange(DoCandidates(rest));
                break;
            case EntryKind.Write:
                if (rest.Count == 0)
                {
                    list.Add(("memory", "Write to startup-config (appsettings.json)"));
                    list.Add(("<cr>", "Validate and save the candidate configuration"));
                }
                else if (rest.Count == 1 && IsPrefixOf("memory", rest[0].Text))
                {
                    list.Add(("<cr>", "Validate and save the candidate configuration"));
                }

                break;
            case EntryKind.No:
            case EntryKind.Default:
            {
                var targets = EntriesFor(edit.Current, includeGlobals: false)
                    .Where(e => e.Kind == EntryKind.Setting || (first.Kind == EntryKind.No && e.Kind == EntryKind.Collection))
                    .ToList();
                if (rest.Count == 0)
                {
                    list.AddRange(targets.Select(e => (e.Keyword, e.Description)));
                }
                else if (Resolve(targets, rest[0].Text) is (MatchOutcome.Unique, { } target))
                {
                    var verb = first.Kind == EntryKind.Default ? "Reset to the default" : "Remove or negate the setting";
                    if (target.Kind == EntryKind.Collection)
                    {
                        if (rest.Count == 1)
                        {
                            list.AddRange(ExistingKeys(edit.Current, target.Collection!).Select(k => (k, "Remove this entry")));
                        }
                        else if (rest.Count == 2 && TryParseKey(target.Collection!, rest[1].Text, out var key, out _) &&
                                 FindEntry(edit.Current, target.Collection!, key) is not null)
                        {
                            list.Add(("<cr>", "Remove '" + target.Collection!.Keyword + " " + key + "'"));
                        }
                    }
                    else if (target.Setting is { Shape: ConfigShape.Map } map && rest.Count == 1 && first.Kind == EntryKind.No)
                    {
                        var current = ConfigurationModel.GetValue(edit.Current.Node, map) as JsonObject;
                        list.AddRange((current ?? new JsonObject()).Select(p => (p.Key, "Remove this override")));
                        list.Add(("<cr>", "Remove all overrides"));
                    }
                    else if (target.Setting is { Shape: ConfigShape.Map } && rest.Count == 2 && first.Kind == EntryKind.No)
                    {
                        list.Add(("<cr>", "Remove this override"));
                    }
                    else if (rest.Count == 1)
                    {
                        list.Add(("<cr>", verb));
                    }
                }

                break;
            }

            case EntryKind.Collection when IsSingleLineCollection(first.Collection!):
                list.AddRange(SecretEntryCandidates(edit, first.Collection!, rest));
                break;
            case EntryKind.Collection:
                list.AddRange(UsernameCandidates(edit, first.Collection!, rest));
                break;
            case EntryKind.Setting:
                list.AddRange(ValueCandidates(first.Setting!, rest));
                break;
            case EntryKind.Section:
                if (rest.Count == 0)
                {
                    list.Add(("<cr>", "Enter " + first.Section!.Mode + " mode"));
                }

                break;
            default:
                if (rest.Count == 0)
                {
                    list.Add(("<cr>", string.Empty));
                }

                break;
        }

        return (start, partial, list);
    }

    /// <summary>Help/completion after <c>show</c> in configuration mode.</summary>
    private static IEnumerable<(string, string)> ShowCandidates(List<Token> rest)
    {
        if (rest.Count == 0)
        {
            return ShowTargets;
        }

        var targets = ShowTargets.Select(t => new Entry(t.Item1, t.Item2, EntryKind.Show)).ToList();
        if (Resolve(targets, rest[0].Text) is not (MatchOutcome.Unique, { } target))
        {
            return [];
        }

        if (target.Keyword == "configuration")
        {
            return rest.Count switch
            {
                1 => [("changes", "Unsaved changes in this session (secrets masked)")],
                2 when IsPrefixOf("changes", rest[1].Text) => [("<cr>", string.Empty)],
                _ => []
            };
        }

        return rest.Count == 1 ? [("<cr>", string.Empty)] : [];
    }

    /// <summary>Help/completion after <c>do</c>: only <c>show</c>, then the EXEC <c>show</c> grammar.</summary>
    private static IEnumerable<(string, string)> DoCandidates(List<Token> rest)
    {
        if (rest.Count == 0)
        {
            return [("show", "Run a read-only privileged EXEC show command")];
        }

        if (!IsPrefixOf("show", rest[0].Text))
        {
            return [];
        }

        var targets = DoShowTargets();
        if (rest.Count == 1)
        {
            return targets.Select(t => (t.Keyword, t.Description));
        }

        if (Resolve(targets, rest[1].Text) is not (MatchOutcome.Unique, { } target))
        {
            return [];
        }

        if (ExecShowNode(target.Keyword) is not { } node)
        {
            return ShowCandidates(rest.Skip(1).ToList());
        }

        // Walk the EXEC catalog for deeper show keywords and arguments.
        var index = 2;
        while (index < rest.Count)
        {
            var child = node.Children.FirstOrDefault(c => c.Token.Equals(rest[index].Text, StringComparison.OrdinalIgnoreCase));
            child ??= node.Children.Where(c => IsPrefixOf(c.Token, rest[index].Text)).ToList() is [var only] ? only : null;
            if (child is null)
            {
                // The token is the node's argument value; nothing may follow it.
                return node.Argument is not null && index == rest.Count - 1 && (node.CommandIdWithArgument ?? node.CommandId) is not null
                    ? [("<cr>", string.Empty)]
                    : [];
            }

            node = child;
            index++;
        }

        var list = node.Children.Select(c => (c.Token, c.Description)).ToList();
        if (node.Argument is { } arg)
        {
            list.Add((arg.DisplayName, arg.Description));
        }

        if (node.CommandId is not null && node.Argument is not { Optional: false })
        {
            list.Add(("<cr>", string.Empty));
        }

        return list;
    }

    /// <summary>Targets accepted after <c>do show</c>: the EXEC show commands plus configuration-session views.</summary>
    private static List<Entry> DoShowTargets() =>
        (CliCommandCatalog.Roots.First(r => r.Token == "show").Children
            .Select(c => new Entry(c.Token, c.Description, EntryKind.Do)))
        .Concat(ShowTargets
            .Where(t => ExecShowNode(t.Item1) is null)
            .Select(t => new Entry(t.Item1, t.Item2, EntryKind.Show)))
        .ToList();

    /// <summary>EXEC catalog node for <c>show &lt;keyword&gt;</c>, or <see langword="null"/>.</summary>
    private static CliCommandNode? ExecShowNode(string keyword) =>
        CliCommandCatalog.Roots.First(r => r.Token == "show").Children
            .FirstOrDefault(c => c.Token.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Help/completion for <c>username &lt;name&gt; [privilege &lt;1-15&gt; | password]</c>.</summary>
    private static IEnumerable<(string, string)> UsernameCandidates(ConfigurationEditSession edit, ConfigCollection collection, List<Token> rest)
    {
        if (rest.Count == 0)
        {
            return ExistingKeys(edit.Current, collection).Select(k => (k, "Existing account"))
                .Append((collection.KeyHelp, "Account name (letters, digits, and . _ @ + -)"));
        }

        if (!TryParseKey(collection, rest[0].Text, out var key, out _))
        {
            return [];
        }

        var exists = FindEntry(edit.Current, collection, key) is not null;
        if (rest.Count == 1)
        {
            return exists
                ? [("privilege", "Set the account's privilege level"),
                   ("password", "Change the account's password (hidden prompt)"),
                   ("<cr>", "Edit this account (enters config-user mode)")]
                : [("privilege", "Privilege level of the new account (required)")];
        }

        var options = new List<Entry> { new("privilege", string.Empty, EntryKind.Setting) };
        if (exists)
        {
            options.Add(new Entry("password", string.Empty, EntryKind.Setting));
        }

        if (Resolve(options, rest[1].Text) is not (MatchOutcome.Unique, { } option))
        {
            return [];
        }

        if (option.Keyword == "password")
        {
            return rest.Count == 2 ? [("<cr>", "Prompt for the new password (hidden input)")] : [];
        }

        return rest.Count switch
        {
            2 => [("<1-15>", "Privilege level")],
            3 when ConfigurationModel.TryParseValue(PrivilegeSetting(collection), rest[2].Text, out _, out _) =>
                [("<cr>", exists ? "Set the privilege level" : "Create the account (prompts for a password)")],
            _ => []
        };
    }

    /// <summary>Help/completion for <c>enable-secret-level &lt;1-15&gt; [hash &lt;hash&gt;]</c>.</summary>
    private static IEnumerable<(string, string)> SecretEntryCandidates(ConfigurationEditSession edit, ConfigCollection collection, List<Token> rest)
    {
        if (rest.Count == 0)
        {
            return ExistingKeys(edit.Current, collection).Select(k => (k, "Existing entry"))
                .Append((collection.KeyHelp, collection.Description));
        }

        if (!TryParseKey(collection, rest[0].Text, out _, out _))
        {
            return [];
        }

        return rest.Count switch
        {
            1 => [("<cr>", "Prompt for the secret (hidden input)"), ("hash", "Set an existing Identity V3 hash")],
            2 when IsPrefixOf("hash", rest[1].Text) => [("<hash>", "Identity V3 hash")],
            3 when IsPrefixOf("hash", rest[1].Text) => [("<cr>", string.Empty)],
            _ => []
        };
    }

    /// <summary>Value help/completion for a setting.</summary>
    private static IEnumerable<(string, string)> ValueCandidates(ConfigSetting setting, List<Token> rest)
    {
        if (setting.IsSecret)
        {
            if (setting.Kind == ConfigValueKind.HostPrivateKey)
            {
                if (rest.Count == 0)
                {
                    yield return ("generate", "Generate a new SSH host key");
                }
                else if (rest.Count == 1 && IsPrefixOf("generate", rest[0].Text))
                {
                    yield return ("<cr>", string.Empty);
                }

                yield break;
            }

            var hashable = setting.Kind is ConfigValueKind.PasswordHash or ConfigValueKind.EnableSecretHash;
            if (rest.Count == 0)
            {
                yield return ("<cr>", "Prompt for the value (hidden input)");
                if (hashable)
                {
                    yield return ("hash", "Set an existing Identity V3 hash");
                }
            }
            else if (hashable && IsPrefixOf("hash", rest[0].Text))
            {
                if (rest.Count == 1)
                {
                    yield return ("<hash>", "Identity V3 hash");
                }
                else if (rest.Count == 2)
                {
                    yield return ("<cr>", string.Empty);
                }
            }

            yield break;
        }

        var freeText = setting.Kind is ConfigValueKind.Text or ConfigValueKind.DirectoryPath or ConfigValueKind.PublicKeyLine;
        switch (setting.Shape)
        {
            case ConfigShape.List when rest.Count == 0:
                yield return ("add", "Add a value");
                yield return ("remove", "Remove a value");
                yield return ("set", "Replace all values");
                yield break;
            case ConfigShape.List when rest.Count == 1:
                foreach (var value in ScalarCandidates(setting))
                {
                    yield return value;
                }

                yield break;
            case ConfigShape.List:
                if (IsPrefixOf("set", rest[0].Text) && !freeText)
                {
                    foreach (var value in ScalarCandidates(setting))
                    {
                        yield return value;
                    }
                }

                if (rest.Count == 2 || freeText || IsPrefixOf("set", rest[0].Text))
                {
                    yield return ("<cr>", string.Empty);
                }

                yield break;
            case ConfigShape.Map when rest.Count == 0:
                yield return ("<source>", "Log source (for example Microsoft.Hosting)");
                yield break;
            case ConfigShape.Map when rest.Count == 1:
                foreach (var value in ScalarCandidates(setting))
                {
                    yield return value;
                }

                yield break;
            case ConfigShape.Map when rest.Count == 2:
                yield return ("<cr>", string.Empty);
                yield break;
            case ConfigShape.Scalar when rest.Count == 0:
                foreach (var value in ScalarCandidates(setting))
                {
                    yield return value;
                }

                if (setting.Kind == ConfigValueKind.Boolean)
                {
                    yield return ("<cr>", "Set to true");
                }

                yield break;
            case ConfigShape.Scalar when rest.Count == 1 || freeText:
                yield return ("<cr>", string.Empty);
                yield break;
        }
    }

    /// <summary>Value tokens for a scalar kind.</summary>
    private static IEnumerable<(string, string)> ScalarCandidates(ConfigSetting setting)
    {
        switch (setting.Kind)
        {
            case ConfigValueKind.Boolean:
                foreach (var value in new[] { "true", "false", "enable", "disable" })
                {
                    yield return (value, setting.Description);
                }

                yield break;
            case ConfigValueKind.Enum:
                foreach (var value in setting.AllowedValues)
                {
                    yield return (value, setting.Description);
                }

                yield break;
            default:
                yield return (setting.ValueHelp, setting.Description);
                yield break;
        }
    }

    /// <summary><c>show</c> targets in configuration mode.</summary>
    private static readonly (string, string)[] ShowTargets =
    [
        ("running-config", "Configuration in use (secrets masked)"),
        ("startup-config", "appsettings.json (secrets masked)"),
        ("candidate-config", "This session's candidate configuration (secrets masked)"),
        ("configuration", "Configuration session information")
    ];

    /// <summary>Executes <c>show ...</c> in configuration mode.</summary>
    private ConfigurationLineResult ExecuteShow(ConfigurationEditSession edit, List<Token> tokens, string prompt, string line)
    {
        if (tokens.Count < 2)
        {
            return Continue(Incomplete);
        }

        var targets = ShowTargets.Select(t => new Entry(t.Item1, t.Item2, EntryKind.Show)).ToList();
        var (outcome, target) = Resolve(targets, tokens[1].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return Error(outcome, prompt, line, tokens[1]);
        }

        if (target!.Keyword == "configuration")
        {
            if (tokens.Count < 3)
            {
                return Continue(Incomplete);
            }

            if (tokens.Count > 3 || !"changes".StartsWith(tokens[2].Text, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(prompt, line, tokens[Math.Min(tokens.Count - 1, 3)]);
            }

            var diff = ConfigurationModel.Diff(edit.Baseline, edit.Candidate);
            return Continue(diff.Count == 0
                ? "No unsaved changes."
                : "Unsaved changes (- before, + after, * secret changed):\r\n" + string.Join("\r\n", diff));
        }

        if (tokens.Count > 2)
        {
            return Invalid(prompt, line, tokens[2]);
        }

        return Continue(target.Keyword switch
        {
            "running-config" => _management.RenderRunning(),
            "startup-config" => _management.RenderStartup(),
            _ => ConfigurationModel.Render(edit.Candidate, "Candidate configuration (unsaved changes included):")
        });
    }

    /// <summary>
    /// Executes <c>do show ...</c>. Configuration-session views run here; every other target is handed to
    /// the EXEC dispatcher, which authorizes it like any EXEC line. Nothing but <c>show</c> is accepted.
    /// </summary>
    private ConfigurationLineResult ExecuteDo(ConfigurationEditSession edit, List<Token> tokens, string prompt, string line)
    {
        if (tokens.Count < 2)
        {
            return Continue(Incomplete);
        }

        if (!IsPrefixOf("show", tokens[1].Text))
        {
            return Continue(Caret(prompt, tokens[1]) + "\r\n% Invalid input detected at '^' marker.\r\n" +
                            "% 'do' runs read-only EXEC 'show' commands only.");
        }

        if (tokens.Count < 3)
        {
            return Continue(Incomplete);
        }

        var (outcome, target) = Resolve(DoShowTargets(), tokens[2].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return Error(outcome, prompt, line, tokens[2]);
        }

        if (target!.Kind == EntryKind.Show)
        {
            return ExecuteShow(edit, tokens.Skip(1).ToList(), prompt, line);
        }

        var command = "show " + target.Keyword + (tokens.Count > 3 ? " " + line[tokens[3].Start..].Trim() : string.Empty);
        return new ConfigurationLineResult(ConfigurationLineAction.ExecuteExec, ExecCommand: command);
    }

    /// <summary>Formats <c>validate</c>.</summary>
    private string FormatValidation(ConfigurationEditSession edit)
    {
        var result = _management.Validate(edit.Candidate);
        if (result.IsValid)
        {
            return "Candidate configuration is valid.";
        }

        return "% Configuration validation failed.\r\n" + string.Join("\r\n", result.Failures.Select(f => "%   " + f));
    }

    /// <summary>Enters a nested section.</summary>
    private static ConfigurationLineResult EnterSection(ConfigurationEditSession edit, ConfigSection section)
    {
        var node = ConfigurationModel.ObjectAt(edit.Current.Node, section.JsonPath, create: true)!;
        edit.Push(new ConfigurationFrame(section.Mode, Join(edit.Current.Path, section.Keyword), section, null, node));
        return new ConfigurationLineResult(ConfigurationLineAction.Continue);
    }

    /// <summary>
    /// Executes <c>username &lt;name&gt;</c> (edit an existing account), <c>username &lt;name&gt; privilege &lt;1-15&gt;</c>
    /// (set the level, or create the account behind a hidden password prompt), or
    /// <c>username &lt;name&gt; password</c> (change an existing account's password at a hidden prompt).
    /// </summary>
    private static ConfigurationLineResult ExecuteUsername(
        ConfigurationEditSession edit,
        ConfigCollection collection,
        string line,
        List<Token> tokens,
        string prompt)
    {
        if (tokens.Count < 2)
        {
            return Continue(Incomplete);
        }

        if (!TryParseKey(collection, tokens[1].Text, out var key, out var error))
        {
            return InvalidArgument(prompt, tokens[1], error);
        }

        var item = FindEntry(edit.Current, collection, key);
        var name = collection.Keyword + " " + key;
        if (tokens.Count == 2)
        {
            return item is null
                ? Continue(Incomplete + "\r\n% A new account needs a privilege level: " + name + " privilege <1-15>")
                : EnterEntry(edit, collection, item, key);
        }

        var options = new List<Entry>
        {
            new("privilege", string.Empty, EntryKind.Setting),
            new("password", string.Empty, EntryKind.Setting)
        };
        var (outcome, option) = Resolve(options, tokens[2].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return Error(outcome, prompt, line, tokens[2]);
        }

        var password = collection.Settings.First(s => s.Kind == ConfigValueKind.PasswordHash);
        if (option!.Keyword == "password")
        {
            if (tokens.Count > 3)
            {
                return new ConfigurationLineResult(ConfigurationLineAction.Continue, PlaintextRejected + ".", ForgetHistory: true);
            }

            return item is null
                ? Continue("% Account '" + key + "' does not exist. Create it with '" + name + " privilege <1-15>'.")
                : new ConfigurationLineResult(
                    ConfigurationLineAction.PromptSecret,
                    Secret: new ConfigurationSecretRequest(password, item, ConfigurationModel.AsString(item[collection.KeyJsonName]) ?? key));
        }

        if (tokens.Count < 4)
        {
            return Continue(Incomplete);
        }

        if (tokens.Count > 4)
        {
            // Trailing text here is often a habitual inline password, so the line never reaches history.
            return Continue(Caret(prompt, tokens[4]) + "\r\n% Invalid input detected at '^' marker.\r\n" +
                            "% Passwords are never accepted on the command line; '" + name + " privilege <1-15>' prompts for one.")
                with { ForgetHistory = true };
        }

        var privilege = PrivilegeSetting(collection);
        if (!ConfigurationModel.TryParseValue(privilege, tokens[3].Text, out var level, out var levelError))
        {
            return InvalidArgument(prompt, tokens[3], levelError);
        }

        if (item is not null)
        {
            ConfigurationModel.SetValue(item, privilege, level);
            return new ConfigurationLineResult(ConfigurationLineAction.Continue);
        }

        var created = NewEntry(collection, key);
        ConfigurationModel.SetValue(created, privilege, level);
        var node = edit.Current.Node;
        return new ConfigurationLineResult(
            ConfigurationLineAction.PromptSecret,
            Secret: new ConfigurationSecretRequest(password, created, key, () => AddEntry(node, collection, created), name));
    }

    /// <summary>
    /// Executes <c>enable-secret-level &lt;1-15&gt;</c> (hidden prompt) or
    /// <c>enable-secret-level &lt;1-15&gt; hash &lt;hash&gt;</c>, creating or replacing the entry.
    /// </summary>
    private ConfigurationLineResult ExecuteSecretEntry(
        ConfigurationEditSession edit,
        ConfigCollection collection,
        string line,
        List<Token> tokens,
        string prompt)
    {
        if (tokens.Count < 2)
        {
            return Continue(Incomplete);
        }

        if (!TryParseKey(collection, tokens[1].Text, out var key, out var error))
        {
            return InvalidArgument(prompt, tokens[1], error);
        }

        var setting = collection.Settings[0];
        var name = collection.Keyword + " " + key;
        var node = edit.Current.Node;
        var existing = FindEntry(edit.Current, collection, key);
        var target = existing ?? NewEntry(collection, key);
        Action? add = existing is null ? () => AddEntry(node, collection, target) : null;
        if (tokens.Count == 2)
        {
            return new ConfigurationLineResult(
                ConfigurationLineAction.PromptSecret,
                Secret: new ConfigurationSecretRequest(setting, target, OperatorPasswordHasher.EnableSecretHashSubject, add, name));
        }

        if (!IsPrefixOf("hash", tokens[2].Text))
        {
            return new ConfigurationLineResult(
                ConfigurationLineAction.Continue,
                PlaintextRejected + ", or use '" + name + " hash <hash>'.",
                ForgetHistory: true);
        }

        if (tokens.Count != 4)
        {
            var failure = tokens.Count < 4 ? Continue(Incomplete) : Invalid(prompt, line, tokens[4]);
            return failure with { ForgetHistory = true };
        }

        if (!_hasher.IsWellFormedHash(tokens[3].Text))
        {
            return new ConfigurationLineResult(ConfigurationLineAction.Continue, "% Invalid value. An Identity V3 hash is required.", ForgetHistory: true);
        }

        ConfigurationModel.SetValue(target, setting, JsonValue.Create(tokens[3].Text));
        add?.Invoke();
        return new ConfigurationLineResult(ConfigurationLineAction.Continue, ForgetHistory: true);
    }

    /// <summary>
    /// Whether a collection's entries are configured on one line in the parent context (no sub-mode), as
    /// <c>enable-secret-level</c> is. The schema guarantees such entries hold exactly one hashed secret, and
    /// that every collection with a sub-mode is an operator-account collection (<c>username</c>).
    /// </summary>
    private static bool IsSingleLineCollection(ConfigCollection collection) => collection.Mode is null;

    /// <summary>The privilege-level setting of an account collection.</summary>
    private static ConfigSetting PrivilegeSetting(ConfigCollection collection) =>
        collection.Settings.First(s => s.Kind == ConfigValueKind.PrivilegeLevel);

    /// <summary>Finds an entry by key in the current context.</summary>
    private static JsonObject? FindEntry(ConfigurationFrame frame, ConfigCollection collection, string key) =>
        frame.Node[collection.JsonName] is JsonArray array ? ConfigurationModel.FindItem(array, collection, key) : null;

    /// <summary>Creates a detached entry with its key and default settings.</summary>
    private static JsonObject NewEntry(ConfigCollection collection, string key)
    {
        var item = new JsonObject(ConfigurationModel.NodeOptions) { [collection.KeyJsonName] = JsonValue.Create(key) };
        foreach (var setting in collection.Settings)
        {
            ConfigurationModel.SetValue(item, setting, setting.Default());
        }

        return item;
    }

    /// <summary>Adds an entry to a collection, creating the array when absent.</summary>
    private static void AddEntry(JsonObject node, ConfigCollection collection, JsonObject item)
    {
        if (node[collection.JsonName] is not JsonArray array)
        {
            array = new JsonArray();
            node[collection.JsonName] = array;
        }

        array.Add(item);
    }

    /// <summary>Enters an entry's sub-mode.</summary>
    private static ConfigurationLineResult EnterEntry(ConfigurationEditSession edit, ConfigCollection collection, JsonObject item, string key)
    {
        var itemKey = ConfigurationModel.AsString(item[collection.KeyJsonName]) ?? key;
        edit.Push(new ConfigurationFrame(collection.Mode!, Join(edit.Current.Path, collection.Keyword + " " + itemKey), null, collection, item));
        return new ConfigurationLineResult(ConfigurationLineAction.Continue);
    }

    /// <summary>Validates a collection key.</summary>
    private static bool TryParseKey(ConfigCollection collection, string raw, out string key, out string error)
    {
        key = raw;
        error = string.Empty;
        if (collection.KeyKind == ConfigValueKind.PrivilegeLevel)
        {
            var setting = new ConfigSetting { Keyword = collection.Keyword, JsonName = collection.KeyJsonName, Description = collection.Description, Kind = ConfigValueKind.PrivilegeLevel };
            if (!ConfigurationModel.TryParseValue(setting, raw, out var value, out var parseError))
            {
                error = parseError ?? "% Invalid value.";
                return false;
            }

            key = ConfigurationModel.AsString(value)!;
            return true;
        }

        if (!OperatorUsernameRules.IsValid(raw))
        {
            error = $"% Invalid value. Usernames are {OperatorUsernameRules.MinLength}-{OperatorUsernameRules.MaxLength} characters: letters, digits, and . _ @ + -";
            return false;
        }

        key = OperatorUsernameRules.NormalizeKey(raw);
        return true;
    }

    /// <summary>Executes <c>no ...</c> / <c>default ...</c>.</summary>
    private static ConfigurationLineResult ExecuteNegation(
        ConfigurationEditSession edit,
        bool isDefault,
        string line,
        List<Token> tokens,
        string prompt)
    {
        if (tokens.Count < 2)
        {
            return Continue(Incomplete);
        }

        var targets = EntriesFor(edit.Current, includeGlobals: false)
            .Where(e => e.Kind == EntryKind.Setting || (!isDefault && e.Kind == EntryKind.Collection))
            .ToList();
        var (outcome, target) = Resolve(targets, tokens[1].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return Error(outcome, prompt, line, tokens[1]);
        }

        var node = edit.Current.Node;
        if (target!.Kind == EntryKind.Collection)
        {
            var collection = target.Collection!;
            if (tokens.Count < 3)
            {
                return Continue(Incomplete);
            }

            if (tokens.Count > 3)
            {
                return Invalid(prompt, line, tokens[3]);
            }

            if (!TryParseKey(collection, tokens[2].Text, out var key, out var error))
            {
                return InvalidArgument(prompt, tokens[2], error);
            }

            if (node[collection.JsonName] is not JsonArray array ||
                ConfigurationModel.FindItem(array, collection, key) is not { } item)
            {
                return Continue($"% Entry '{collection.Keyword} {key}' does not exist.");
            }

            array.Remove(item);
            return new ConfigurationLineResult(ConfigurationLineAction.Continue);
        }

        var setting = target.Setting!;
        if (setting.Shape == ConfigShape.Map && !isDefault && tokens.Count == 3)
        {
            var map = ConfigurationModel.GetValue(node, setting) as JsonObject;
            var existing = map?.FirstOrDefault(p => p.Key.Equals(tokens[2].Text, StringComparison.OrdinalIgnoreCase));
            if (map is null || existing?.Key is not { } existingKey)
            {
                return Continue($"% No {setting.Keyword} is configured for '{tokens[2].Text}'.");
            }

            map.Remove(existingKey);
            return new ConfigurationLineResult(ConfigurationLineAction.Continue);
        }

        if (tokens.Count > 2)
        {
            return Invalid(prompt, line, tokens[2]);
        }

        JsonNode? value;
        if (isDefault)
        {
            value = setting.Default();
        }
        else if (setting.Shape == ConfigShape.List)
        {
            value = new JsonArray();
        }
        else if (setting.Shape == ConfigShape.Map)
        {
            value = new JsonObject();
        }
        else if (setting.Kind == ConfigValueKind.Boolean)
        {
            value = JsonValue.Create(false);
        }
        else if (setting.IsSecret)
        {
            value = JsonValue.Create(string.Empty);
        }
        else if (setting.Nullable)
        {
            value = null;
        }
        else
        {
            value = setting.Default();
        }

        ConfigurationModel.SetValue(node, setting, value);
        return new ConfigurationLineResult(ConfigurationLineAction.Continue);
    }

    /// <summary>Executes <c>&lt;setting&gt; ...</c>.</summary>
    private ConfigurationLineResult ExecuteSetting(
        ConfigurationEditSession edit,
        ConfigSetting setting,
        string line,
        List<Token> tokens,
        string prompt)
    {
        var node = edit.Current.Node;
        if (setting.IsSecret)
        {
            return ExecuteSecret(edit, setting, line, tokens, prompt);
        }

        switch (setting.Shape)
        {
            case ConfigShape.List:
                return ExecuteList(node, setting, line, tokens, prompt);
            case ConfigShape.Map:
            {
                if (tokens.Count < 3)
                {
                    return Continue(Incomplete);
                }

                if (tokens.Count > 3)
                {
                    return Invalid(prompt, line, tokens[3]);
                }

                var source = tokens[1].Text;
                if (source.Length > 256 || !ConfigurationModel.LogSourceRegex().IsMatch(source))
                {
                    return InvalidArgument(prompt, tokens[1], "% Invalid value. A log source name such as Microsoft.Hosting is required.");
                }

                if (!ConfigurationModel.TryParseValue(setting, tokens[2].Text, out var level, out var error))
                {
                    return InvalidArgument(prompt, tokens[2], error);
                }

                if (ConfigurationModel.GetValue(node, setting) is not JsonObject map)
                {
                    map = new JsonObject(ConfigurationModel.NodeOptions);
                    ConfigurationModel.SetValue(node, setting, map);
                    map = (JsonObject)ConfigurationModel.GetValue(node, setting)!;
                }

                var existing = map.FirstOrDefault(p => p.Key.Equals(source, StringComparison.OrdinalIgnoreCase)).Key;
                if (existing is not null)
                {
                    map.Remove(existing);
                }

                map[existing ?? source] = level;
                return new ConfigurationLineResult(ConfigurationLineAction.Continue);
            }

            default:
            {
                var raw = tokens.Count > 1 ? line[tokens[1].Start..].Trim() : string.Empty;
                if (raw.Length == 0)
                {
                    if (setting.Kind != ConfigValueKind.Boolean)
                    {
                        return Continue(Incomplete);
                    }

                    raw = "true";
                }
                else if (setting.Kind is not (ConfigValueKind.Text or ConfigValueKind.DirectoryPath) && tokens.Count > 2)
                {
                    return Invalid(prompt, line, tokens[2]);
                }

                if (!ConfigurationModel.TryParseValue(setting, raw, out var value, out var error))
                {
                    return InvalidArgument(prompt, tokens[1], error);
                }

                ConfigurationModel.SetValue(node, setting, value);
                return new ConfigurationLineResult(ConfigurationLineAction.Continue);
            }
        }
    }

    /// <summary>Executes <c>&lt;list&gt; add|remove|set ...</c>.</summary>
    private static ConfigurationLineResult ExecuteList(JsonObject node, ConfigSetting setting, string line, List<Token> tokens, string prompt)
    {
        if (tokens.Count < 2)
        {
            return Continue(Incomplete);
        }

        var operations = new List<Entry>
        {
            new("add", "Add a value", EntryKind.Setting),
            new("remove", "Remove a value", EntryKind.Setting),
            new("set", "Replace all values", EntryKind.Setting)
        };
        var (outcome, operation) = Resolve(operations, tokens[1].Text);
        if (outcome != MatchOutcome.Unique)
        {
            return Error(outcome, prompt, line, tokens[1]);
        }

        if (tokens.Count < 3)
        {
            return Continue(Incomplete);
        }

        // Public-key lines contain spaces and form one value; other list values are single tokens.
        var rawValues = setting.Kind == ConfigValueKind.PublicKeyLine
            ? [line[tokens[2].Start..].Trim()]
            : tokens.Skip(2).Select(t => t.Text).ToList();
        if (operation!.Keyword != "set" && rawValues.Count > 1)
        {
            return Invalid(prompt, line, tokens[3]);
        }

        var parsed = new List<string>();
        for (var i = 0; i < rawValues.Count; i++)
        {
            if (!ConfigurationModel.TryParseValue(setting, rawValues[i], out var value, out var error))
            {
                return InvalidArgument(prompt, tokens[2 + i], error);
            }

            parsed.Add(ConfigurationModel.AsString(value)!);
        }

        var current = ConfigurationModel.AsList(ConfigurationModel.GetValue(node, setting));
        var comparer = StringComparer.OrdinalIgnoreCase;
        switch (operation.Keyword)
        {
            case "add":
                if (current.Contains(parsed[0], comparer))
                {
                    return Continue("% Value is already present.");
                }

                current.Add(parsed[0]);
                break;
            case "remove":
                var index = current.FindIndex(v => comparer.Equals(v, parsed[0]));
                if (index < 0)
                {
                    return Continue("% Value not found.");
                }

                current.RemoveAt(index);
                break;
            default:
                current = parsed.Distinct(comparer).ToList();
                break;
        }

        ConfigurationModel.SetValue(node, setting, new JsonArray(current.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
        return new ConfigurationLineResult(ConfigurationLineAction.Continue);
    }

    /// <summary>Executes a secret keyword (hidden prompt, existing hash, or host-key generation).</summary>
    private ConfigurationLineResult ExecuteSecret(
        ConfigurationEditSession edit,
        ConfigSetting setting,
        string line,
        List<Token> tokens,
        string prompt)
    {
        var node = edit.Current.Node;
        if (setting.Kind == ConfigValueKind.HostPrivateKey)
        {
            if (tokens.Count == 1)
            {
                return Continue(Incomplete);
            }

            if (tokens.Count == 2 && "generate".StartsWith(tokens[1].Text, StringComparison.OrdinalIgnoreCase))
            {
                var key = SshKeyMaterial.GenerateHostKeyPair();
                ConfigurationModel.SetValue(node, setting, JsonValue.Create(SshKeyMaterial.ExportPrivateKeyPem(key)));
                return Continue("New SSH host key generated (" + SshKeyMaterial.ComputeFingerprint(key) +
                                "). It takes effect after 'write' and a server restart.");
            }

            return new ConfigurationLineResult(ConfigurationLineAction.Continue, PlaintextRejected + ", or use 'host-private-key generate'.", ForgetHistory: true);
        }

        if (tokens.Count == 1)
        {
            var subject = setting.Kind == ConfigValueKind.PasswordHash
                ? ConfigurationModel.AsString(node[edit.Current.Collection?.KeyJsonName ?? string.Empty]) ?? string.Empty
                : OperatorPasswordHasher.EnableSecretHashSubject;
            return new ConfigurationLineResult(ConfigurationLineAction.PromptSecret, Secret: new ConfigurationSecretRequest(setting, node, subject));
        }

        var isHashKind = setting.Kind is ConfigValueKind.PasswordHash or ConfigValueKind.EnableSecretHash;
        if (isHashKind && tokens[1].Text.Equals("hash", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count != 3)
            {
                return tokens.Count < 3 ? Continue(Incomplete) : Invalid(prompt, line, tokens[3]);
            }

            if (!_hasher.IsWellFormedHash(tokens[2].Text))
            {
                return new ConfigurationLineResult(ConfigurationLineAction.Continue, "% Invalid value. An Identity V3 hash is required.", ForgetHistory: true);
            }

            ConfigurationModel.SetValue(node, setting, JsonValue.Create(tokens[2].Text));
            return new ConfigurationLineResult(ConfigurationLineAction.Continue, ForgetHistory: true);
        }

        return new ConfigurationLineResult(
            ConfigurationLineAction.Continue,
            PlaintextRejected + (isHashKind ? ", or use '" + setting.Keyword + " hash <hash>'." : "."),
            ForgetHistory: true);
    }

    /// <summary>Keywords available in a context.</summary>
    private static List<Entry> EntriesFor(ConfigurationFrame frame, bool includeGlobals)
    {
        var entries = new List<Entry>();
        entries.AddRange(frame.Settings.Select(s => new Entry(s.Keyword, s.Description, EntryKind.Setting, Setting: s)));
        entries.AddRange(frame.Children.Select(c => new Entry(c.Keyword, c.Description, EntryKind.Section, Section: c)));
        entries.AddRange(frame.Collections.Select(c => new Entry(c.Keyword, c.Description, EntryKind.Collection, Collection: c)));
        if (includeGlobals)
        {
            entries.Add(new Entry("exit", "Exit from the current configuration level", EntryKind.Exit));
            entries.Add(new Entry("end", "Exit configuration mode (unsaved changes are discarded)", EntryKind.End));
            entries.Add(new Entry("write", "Validate and save the candidate configuration", EntryKind.Write));
            entries.Add(new Entry("show", "Show running, startup, or candidate configuration", EntryKind.Show));
            entries.Add(new Entry("validate", "Validate the candidate configuration without saving", EntryKind.Validate));
            entries.Add(new Entry("help", "Show the commands available at this level", EntryKind.Help));
            entries.Add(new Entry("no", "Negate a setting or remove an entry", EntryKind.No));
            entries.Add(new Entry("default", "Reset a setting to its default", EntryKind.Default));
            entries.Add(new Entry("do", "Run a read-only EXEC show command (for example 'do show users')", EntryKind.Do));
        }

        return entries;
    }

    /// <summary>Help syntax for an entry.</summary>
    private static string Syntax(Entry entry) => entry.Kind switch
    {
        EntryKind.Collection when IsSingleLineCollection(entry.Collection!) =>
            entry.Keyword + " " + entry.Collection!.KeyHelp + " [hash <hash>]",
        EntryKind.Collection => entry.Keyword + " " + entry.Collection!.KeyHelp + " [privilege <1-15> | password]",
        EntryKind.Do => "do show <command>",
        EntryKind.Setting when entry.Setting!.Shape == ConfigShape.List => entry.Keyword + " add|remove|set <value>",
        EntryKind.Setting when entry.Setting!.Shape == ConfigShape.Map => entry.Keyword + " <source> <level>",
        EntryKind.Setting => entry.Keyword + " " + entry.Setting!.ValueHelp,
        _ => entry.Keyword
    };

    /// <summary>Existing collection keys in the current context.</summary>
    private static IEnumerable<string> ExistingKeys(ConfigurationFrame frame, ConfigCollection collection) =>
        (frame.Node[collection.JsonName] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(i => ConfigurationModel.AsString(i[collection.KeyJsonName]))
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!);

    /// <summary>Resolves a keyword: exact match wins, otherwise a unique prefix.</summary>
    private static (MatchOutcome Outcome, Entry? Entry) Resolve(IReadOnlyList<Entry> entries, string token)
    {
        var exact = entries.FirstOrDefault(e => e.Keyword.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return (MatchOutcome.Unique, exact);
        }

        var prefixed = entries.Where(e => e.Keyword.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefixed.Count switch
        {
            0 => (MatchOutcome.None, null),
            1 => (MatchOutcome.Unique, prefixed[0]),
            _ => (MatchOutcome.Ambiguous, null)
        };
    }

    /// <summary>Splits a line into tokens with offsets.</summary>
    private static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i > start)
            {
                tokens.Add(new Token(line[start..i], start));
            }
        }

        return tokens;
    }

    /// <summary>Returns an error result when extra tokens follow a bare keyword.</summary>
    private static ConfigurationLineResult? NoArguments(List<Token> tokens, string prompt, string line) =>
        tokens.Count > 1 ? Invalid(prompt, line, tokens[1]) : null;

    /// <summary>Unknown/ambiguous keyword error.</summary>
    private static ConfigurationLineResult Error(MatchOutcome outcome, string prompt, string line, Token token) =>
        outcome == MatchOutcome.Ambiguous
            ? Continue("% Ambiguous command:  \"" + line.Trim() + "\"")
            : Invalid(prompt, line, token);

    /// <summary>Cisco caret error under the offending token.</summary>
    private static ConfigurationLineResult Invalid(string prompt, string line, Token token) =>
        Continue(Caret(prompt, token) + "\r\n% Invalid input detected at '^' marker.");

    /// <summary>A well-formed command whose argument failed validation: caret under the argument plus the reason.</summary>
    private static ConfigurationLineResult InvalidArgument(string prompt, Token token, string? reason) =>
        Continue(Caret(prompt, token) + "\r\n" + (string.IsNullOrEmpty(reason) ? "% Invalid value." : reason));

    /// <summary>Caret line under <paramref name="token"/> as echoed after the prompt.</summary>
    private static string Caret(string prompt, Token token) => new string(' ', prompt.Length + 1 + token.Start) + "^";

    /// <summary>Whether <paramref name="token"/> is a non-empty abbreviation of <paramref name="keyword"/>.</summary>
    private static bool IsPrefixOf(string keyword, string token) =>
        token.Length > 0 && keyword.StartsWith(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>Continue with output.</summary>
    private static ConfigurationLineResult Continue(string? output) => new(ConfigurationLineAction.Continue, output);

    /// <summary>Joins CLI path segments.</summary>
    private static string Join(string prefix, string segment) => prefix.Length == 0 ? segment : prefix + " " + segment;

    /// <summary>Longest common case-insensitive prefix.</summary>
    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        var prefix = values[0];
        foreach (var value in values.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < value.Length &&
                   char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(value[length]))
            {
                length++;
            }

            prefix = prefix[..length];
        }

        return prefix;
    }
}

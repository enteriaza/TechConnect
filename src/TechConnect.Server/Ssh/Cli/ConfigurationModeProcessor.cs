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
    Write
}

/// <summary>
/// Pending hidden-input secret assignment.
/// </summary>
/// <param name="Setting">Secret setting.</param>
/// <param name="Container">Candidate object that owns the setting.</param>
/// <param name="Subject">Hash subject (username for passwords).</param>
internal sealed record ConfigurationSecretRequest(ConfigSetting Setting, JsonObject Container, string Subject)
{
    /// <summary>First prompt text.</summary>
    public string Prompt => Setting.Kind == ConfigValueKind.PasswordHash ? "Enter password: " : "Enter secret: ";

    /// <summary>Confirmation prompt text.</summary>
    public string ConfirmPrompt => Setting.Kind == ConfigValueKind.PasswordHash ? "Confirm password: " : "Confirm secret: ";
}

/// <summary>
/// Result of one configuration-mode line.
/// </summary>
/// <param name="Action">Follow-up action.</param>
/// <param name="Output">Text to print (no trailing newline).</param>
/// <param name="Secret">Secret request for <see cref="ConfigurationLineAction.PromptSecret"/>.</param>
/// <param name="ForgetHistory">The line may contain secret material and must be dropped from history.</param>
internal sealed record ConfigurationLineResult(
    ConfigurationLineAction Action,
    string? Output = null,
    ConfigurationSecretRequest? Secret = null,
    bool ForgetHistory = false);

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
            case EntryKind.No:
            case EntryKind.Default:
                return ExecuteNegation(edit, entry.Kind == EntryKind.Default, line, tokens, prompt);
            case EntryKind.Section:
                return NoArguments(tokens, prompt, line) ?? EnterSection(edit, entry.Section!);
            case EntryKind.Collection:
                return EnterCollection(edit, entry.Collection!, line, tokens, prompt);
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
        if (!string.Equals(secret, confirmation, StringComparison.Ordinal))
        {
            return "% Entries do not match. Value not changed.";
        }

        if (string.IsNullOrEmpty(secret))
        {
            return "% An empty value is not accepted. Use 'no " + request.Setting.Keyword + "' to remove it.";
        }

        if (secret.Length > request.Setting.MaxLength || secret.Any(char.IsControl))
        {
            return "% Invalid value. Value not changed.";
        }

        var stored = request.Setting.Kind switch
        {
            ConfigValueKind.PasswordHash => _hasher.HashPassword(request.Subject, secret),
            ConfigValueKind.EnableSecretHash => _hasher.HashEnableSecret(secret),
            _ => secret
        };
        ConfigurationModel.SetValue(request.Container, request.Setting, JsonValue.Create(stored));
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
        var entries = candidates
            .Where(c => partial.Length == 0 || c.Token.StartsWith(partial, StringComparison.OrdinalIgnoreCase) || c.Token.StartsWith('<'))
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
                if (rest.Count == 0)
                {
                    list.AddRange(ShowTargets);
                }
                else if (rest.Count == 1 && "configuration".StartsWith(rest[0].Text, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(("changes", "Unsaved changes in this session (secrets masked)"));
                }

                break;
            case EntryKind.Write:
                if (rest.Count == 0)
                {
                    list.Add(("memory", "Write to startup-config (appsettings.json)"));
                    list.Add(("<cr>", string.Empty));
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
                    if (target.Kind == EntryKind.Collection && rest.Count == 1)
                    {
                        list.AddRange(ExistingKeys(edit.Current, target.Collection!).Select(k => (k, target.Collection!.Description)));
                        list.Add((target.Collection!.KeyHelp, target.Collection.Description));
                    }
                    else if (target.Setting is { Shape: ConfigShape.Map } map && rest.Count == 1 && first.Kind == EntryKind.No)
                    {
                        var current = ConfigurationModel.GetValue(edit.Current.Node, map) as JsonObject;
                        list.AddRange((current ?? new JsonObject()).Select(p => (p.Key, "Remove this override")));
                        list.Add(("<cr>", "Remove all overrides"));
                    }
                    else if (rest.Count == 1)
                    {
                        list.Add(("<cr>", string.Empty));
                    }
                }

                break;
            }

            case EntryKind.Collection:
                if (rest.Count == 0)
                {
                    list.AddRange(ExistingKeys(edit.Current, first.Collection!).Select(k => (k, first.Collection!.Description)));
                    list.Add((first.Collection!.KeyHelp, first.Collection.Description));
                }
                else if (rest.Count == 1)
                {
                    list.Add(("<cr>", string.Empty));
                }

                break;
            case EntryKind.Setting:
                list.AddRange(ValueCandidates(first.Setting!, rest));
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

    /// <summary>Value help/completion for a setting.</summary>
    private static IEnumerable<(string, string)> ValueCandidates(ConfigSetting setting, List<Token> rest)
    {
        if (setting.IsSecret)
        {
            if (rest.Count > 0)
            {
                yield break;
            }

            if (setting.Kind == ConfigValueKind.HostPrivateKey)
            {
                yield return ("generate", "Generate a new SSH host key");
                yield break;
            }

            yield return ("<cr>", "Prompt for the value (hidden input)");
            if (setting.Kind is ConfigValueKind.PasswordHash or ConfigValueKind.EnableSecretHash)
            {
                yield return ("hash", "Set an existing Identity V3 hash");
            }

            yield break;
        }

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
            case ConfigShape.Map when rest.Count == 0:
                yield return ("<source>", "Log source (for example Microsoft.Hosting)");
                yield break;
            case ConfigShape.Map when rest.Count == 1:
                foreach (var value in ScalarCandidates(setting))
                {
                    yield return value;
                }

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

    /// <summary>Enters (creating when absent) a collection entry.</summary>
    private static ConfigurationLineResult EnterCollection(
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

        if (tokens.Count > 2)
        {
            return Invalid(prompt, line, tokens[2]);
        }

        if (!TryParseKey(collection, tokens[1].Text, out var key, out var error))
        {
            return Continue(error);
        }

        if (edit.Current.Node[collection.JsonName] is not JsonArray array)
        {
            array = new JsonArray();
            edit.Current.Node[collection.JsonName] = array;
        }

        var item = ConfigurationModel.FindItem(array, collection, key);
        if (item is null)
        {
            item = new JsonObject(ConfigurationModel.NodeOptions) { [collection.KeyJsonName] = JsonValue.Create(key) };
            foreach (var setting in collection.Settings)
            {
                ConfigurationModel.SetValue(item, setting, setting.Default());
            }

            array.Add(item);
        }

        var itemKey = ConfigurationModel.AsString(item[collection.KeyJsonName]) ?? key;
        edit.Push(new ConfigurationFrame(collection.Mode, Join(edit.Current.Path, collection.Keyword + " " + itemKey), null, collection, item));
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
                return Continue(error);
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
                    return Continue("% Invalid value. A log source name such as Microsoft.Hosting is required.");
                }

                if (!ConfigurationModel.TryParseValue(setting, tokens[2].Text, out var level, out var error))
                {
                    return Continue(error);
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
                    return Continue(error);
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
        foreach (var raw in rawValues)
        {
            if (!ConfigurationModel.TryParseValue(setting, raw, out var value, out var error))
            {
                return Continue(error);
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
        }

        return entries;
    }

    /// <summary>Help syntax for an entry.</summary>
    private static string Syntax(Entry entry) => entry.Kind switch
    {
        EntryKind.Collection => entry.Keyword + " " + entry.Collection!.KeyHelp,
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
        Continue(new string(' ', prompt.Length + 1 + token.Start) + "^\r\n% Invalid input detected at '^' marker.");

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

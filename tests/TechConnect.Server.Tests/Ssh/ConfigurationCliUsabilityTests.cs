// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationCliUsabilityTests.cs
//
// Purpose:
// Configuration-mode grammar and discovery tests: context help per level,
// <cr> accuracy, abbreviations, error categories, the username and
// enable-secret-level grammars, and 'do show' routing and restrictions.
//
// -----------------------------------------------------------------------------

using System.Text.Json.Nodes;
using FluentAssertions;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Configuration.Management;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Usability tests for <see cref="ConfigurationModeProcessor"/>.
/// </summary>
public sealed class ConfigurationCliUsabilityTests : IDisposable
{
    /// <summary>Fixture.</summary>
    private readonly ConfigurationFixture _fixture = new();

    /// <summary>Service.</summary>
    private readonly ConfigurationManagementService _service;

    /// <summary>Processor under test.</summary>
    private readonly ConfigurationModeProcessor _processor;

    /// <summary>Level-15 identity.</summary>
    private readonly AuthenticatedOperator _admin = ConfigurationFixture.Admin();

    /// <summary>Edit session.</summary>
    private readonly ConfigurationEditSession _edit;

    /// <summary>Creates the processor and an edit session.</summary>
    public ConfigurationCliUsabilityTests()
    {
        _service = _fixture.CreateService();
        _processor = new ConfigurationModeProcessor(_service, CliTestFactory.CreateAuthorization());
        _edit = _service.BeginEdit(_admin, out _);
    }

    /// <inheritdoc />
    public void Dispose() => _fixture.Dispose();

    /// <summary>Runs a line as the admin.</summary>
    private ConfigurationLineResult Run(string line) => _processor.Execute(_edit, _admin, line);

    /// <summary>Navigates from (config) through space-separated sub-mode keywords.</summary>
    private void Enter(string path)
    {
        foreach (var keyword in path.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Run(keyword).Output.Should().BeNullOrEmpty();
        }
    }

    /// <summary><c>?</c> tokens for <paramref name="line"/> with the cursor at the end.</summary>
    private IReadOnlyList<string> HelpTokens(string line) => _processor.Help(_edit, line, line.Length, 80).Tokens;

    /// <summary>The candidate's local-user array.</summary>
    private JsonArray? LocalUsers() => ConfigPaths.Node(_edit.Candidate, "authentication local")["LocalUsers"] as JsonArray;

    /// <summary>The candidate entry for <paramref name="username"/>.</summary>
    private JsonObject? User(string username) =>
        LocalUsers()?.OfType<JsonObject>().FirstOrDefault(u => (string?)u["Username"] == username);

    /// <summary>Global, auth, and auth-local help list the keywords of that level only.</summary>
    [Fact]
    public void Help_ListsKeywordsPerLevel()
    {
        HelpTokens(string.Empty).Should().Contain(["authentication", "ssh", "do", "exit", "end", "write", "show", "no", "default"])
            .And.NotContain("<cr>").And.NotContain("username");
        HelpTokens("authentication ").Should().Equal("<cr>");

        Enter("authentication");
        HelpTokens(string.Empty).Should().Contain(["local", "radius", "do"]).And.NotContain("ssh").And.NotContain("<cr>");

        Enter("local");
        HelpTokens(string.Empty).Should().Contain(["username", "enable-secret", "enable-secret-level", "password-authentication-enabled"])
            .And.NotContain("<cr>");
    }

    /// <summary>
    /// <c>&lt;cr&gt;</c> is offered exactly when the line as typed would execute.
    /// </summary>
    [Theory]
    [InlineData("", "ssh ", true)]
    [InlineData("", "ssh port ", false)]
    [InlineData("ssh", "port ", false)]
    [InlineData("ssh", "port 2222 ", true)]
    [InlineData("ssh", "port 2222 x", false)]
    [InlineData("ssh", "enabled ", true)]
    [InlineData("ssh", "enabled false ", true)]
    [InlineData("", "authentication ", true)]
    [InlineData("", "write ", true)]
    [InlineData("", "write memory ", true)]
    [InlineData("", "show ", false)]
    [InlineData("", "show running-config ", true)]
    [InlineData("", "show configuration ", false)]
    [InlineData("", "show configuration changes ", true)]
    [InlineData("", "no ", false)]
    [InlineData("", "do ", false)]
    [InlineData("", "do show ", false)]
    [InlineData("", "do show users ", true)]
    [InlineData("", "do show session ", false)]
    [InlineData("", "do show configuration changes ", true)]
    [InlineData("authentication local", "username ", false)]
    [InlineData("authentication local", "username newop ", false)]
    [InlineData("authentication local", "username newop privilege ", false)]
    [InlineData("authentication local", "username newop privilege 15 ", true)]
    [InlineData("authentication local", "username newop privilege 16 ", false)]
    [InlineData("authentication local", "username newop password ", false)]
    [InlineData("authentication local", "username admin ", true)]
    [InlineData("authentication local", "username admin password ", true)]
    [InlineData("authentication local", "no username ", false)]
    [InlineData("authentication local", "no username admin ", true)]
    [InlineData("authentication local", "no username ghost ", false)]
    [InlineData("authentication local", "enable-secret ", true)]
    [InlineData("authentication local", "enable-secret hash ", false)]
    [InlineData("authentication local", "enable-secret-level ", false)]
    [InlineData("authentication local", "enable-secret-level 5 ", true)]
    [InlineData("authentication local", "enable-secret-level 5 hash ", false)]
    public void Help_CrOnlyWhenExecutable(string path, string line, bool expectCr)
    {
        Enter(path);

        var tokens = HelpTokens(line);

        if (expectCr)
        {
            tokens.Should().Contain("<cr>");
        }
        else
        {
            tokens.Should().NotContain("<cr>");
        }
    }

    /// <summary>Username help distinguishes new accounts (privilege required) from existing ones.</summary>
    [Fact]
    public void UsernameHelp_GuidesTowardsPrivilegeAndPassword()
    {
        Enter("authentication local");

        HelpTokens("username ").Should().Contain(["admin", "<username>"]);
        HelpTokens("username admin ").Should().Equal("privilege", "password", "<cr>");
        HelpTokens("username newop ").Should().Equal("privilege");
        HelpTokens("username newop privilege ").Should().Equal("<1-15>");
        _processor.Help(_edit, "username newop privilege 15 ", 28, 80).DisplayText.Should().Contain("prompts for a password");
    }

    /// <summary>Help and completion never execute anything, change the mode, or touch the candidate.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("authentication ")]
    [InlineData("username newop privilege 15 ")]
    [InlineData("username admin password ")]
    [InlineData("no username admin ")]
    [InlineData("enable-secret-level 5 ")]
    [InlineData("do show ")]
    [InlineData("write ")]
    [InlineData("end ")]
    public void HelpAndCompletion_DoNotMutate(string line)
    {
        Enter("authentication local");
        var before = _edit.Candidate.ToJsonString();

        _processor.Help(_edit, line, line.Length, 80);
        _processor.Complete(_edit, line, line.Length, 80);

        _edit.Candidate.ToJsonString().Should().Be(before);
        _edit.Mode.Should().Be("config-auth-local");
    }

    /// <summary>Completion works at the cursor, extends ambiguous prefixes, and offers no placeholders.</summary>
    [Fact]
    public void Completion_UniqueAmbiguousAndMidLine()
    {
        var unique = _processor.Complete(_edit, "authen", 6, 80);
        unique.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        unique.CompletionText.Should().Be("authentication");

        var ambiguous = _processor.Complete(_edit, "e", 1, 80);
        ambiguous.Kind.Should().Be(CliDiscoveryKind.AmbiguousCompletion);
        ambiguous.Tokens.Should().Contain(["end", "exit"]);

        var midLine = _processor.Complete(_edit, "sh running-config", 2, 80);
        midLine.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        midLine.CompletionText.Should().Be("show");
        (midLine.ReplaceStart, midLine.ReplaceEnd).Should().Be((0, 2));

        Enter("authentication local");
        _processor.Complete(_edit, "username newop privilege ", 25, 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>Unique abbreviations resolve, exact matches win over longer keywords, and ambiguity is reported.</summary>
    [Fact]
    public void Abbreviations_UniqueExactAndAmbiguous()
    {
        Run("auth").Output.Should().BeNullOrEmpty();
        Run("loc").Output.Should().BeNullOrEmpty();
        _edit.Mode.Should().Be("config-auth-local");

        // "enable-secret" is also a prefix of "enable-secret-level"; the exact keyword wins.
        Run("enable-secret").Secret!.Setting.Keyword.Should().Be("enable-secret");
        Run("enable-secret-l 5").Secret!.Setting.Keyword.Should().Be("secret");
        Run("enable").Output.Should().Contain("% Ambiguous command:  \"enable\"");
        Run("username admin priv 9").Output.Should().BeNullOrEmpty();
        ((string?)User("admin")!["PrivilegeLevel"]).Should().Be("9");
    }

    /// <summary>Incomplete, invalid (with an accurate caret), ambiguous, and invalid-argument errors are distinct.</summary>
    [Fact]
    public void Errors_AreCategorized_WithAccurateCaret()
    {
        var prompt = ConfigurationModeProcessor.GetPrompt(_edit);
        var invalid = Run("ssh bogus").Output!;
        invalid.Should().StartWith(new string(' ', prompt.Length + 1 + 4) + "^\r\n% Invalid input detected at '^' marker.");

        Run("ss").Output.Should().BeNullOrEmpty();
        prompt = ConfigurationModeProcessor.GetPrompt(_edit);
        Run("port").Output.Should().Be("% Incomplete command.");
        var badValue = Run("port 70000").Output!;
        badValue.Should().Be(new string(' ', prompt.Length + 1 + 5) + "^\r\n% Invalid value. Range is 1-65535.");
        Run("e").Output.Should().StartWith("% Ambiguous command:");
    }

    /// <summary><c>username &lt;new&gt;</c> alone is incomplete and creates nothing.</summary>
    [Fact]
    public void Username_NewWithoutPrivilege_IsIncomplete()
    {
        Enter("authentication local");
        var before = _edit.Candidate.ToJsonString();

        var result = Run("username newop");

        result.Output.Should().StartWith("% Incomplete command.").And.Contain("username newop privilege <1-15>");
        _edit.Candidate.ToJsonString().Should().Be(before);
        _edit.Mode.Should().Be("config-auth-local");
    }

    /// <summary>
    /// <c>username &lt;new&gt; privilege &lt;n&gt;</c> prompts for a hidden password and creates the account
    /// only once it is confirmed, stored as an Identity V3 hash.
    /// </summary>
    [Fact]
    public void Username_Create_PromptsAndHashes_OnlyWhenConfirmed()
    {
        Enter("authentication local");
        var before = _edit.Candidate.ToJsonString();

        var result = Run("username NewOp privilege 5");

        result.Action.Should().Be(ConfigurationLineAction.PromptSecret);
        result.Secret!.Prompt.Should().Be("Enter password: ");
        _edit.Candidate.ToJsonString().Should().Be(before, "nothing exists until the password is confirmed");
        _processor.ApplySecret(result.Secret, "Pass-One-1", "Pass-Two-2").Should().Be("% Entries do not match. 'username NewOp' was not created.");
        _processor.ApplySecret(result.Secret, string.Empty, string.Empty).Should().Contain("was not created");
        _edit.Candidate.ToJsonString().Should().Be(before);

        _processor.ApplySecret(result.Secret, "Pass-One-1", "Pass-One-1").Should().BeNull();

        var user = User("NewOp")!;
        ((string?)user["PrivilegeLevel"]).Should().Be("5");
        var hash = (string)user["PasswordHash"]!;
        new OperatorPasswordHasher().VerifyPassword("NewOp", hash, "Pass-One-1").Should().BeTrue();
        _edit.Candidate.ToJsonString().Should().NotContain("Pass-One-1");
        Run("show configuration changes").Output.Should().NotContain("Pass-One-1").And.NotContain(hash);
        _fixture.ReadFile().Should().NotContain("NewOp", "nothing is persisted until write");
    }

    /// <summary>A cancelled creation prompt leaves no entry (the session never calls ApplySecret).</summary>
    [Fact]
    public void Username_CreateCancelled_LeavesNoEntry()
    {
        Enter("authentication local");
        var before = _edit.Candidate.ToJsonString();

        var result = Run("username newop privilege 3");

        result.Secret!.CancelledMessage.Should().Be("% Cancelled. 'username newop' was not created.");
        _edit.Candidate.ToJsonString().Should().Be(before);
    }

    /// <summary>Privilege levels and usernames are validated by the real rules, with a caret under the argument.</summary>
    [Theory]
    [InlineData("username newop privilege 0", 25)]
    [InlineData("username newop privilege 16", 25)]
    [InlineData("username newop privilege high", 25)]
    [InlineData("username bad/name privilege 5", 9)]
    [InlineData("username admin privilege 99", 25)]
    public void Username_InvalidArguments_AreRejected(string line, int column)
    {
        Enter("authentication local");
        var prompt = ConfigurationModeProcessor.GetPrompt(_edit);
        var before = _edit.Candidate.ToJsonString();

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.Continue);
        result.Output.Should().StartWith(new string(' ', prompt.Length + 1 + column) + "^\r\n% Invalid value");
        _edit.Candidate.ToJsonString().Should().Be(before);
    }

    /// <summary>Existing accounts: privilege changes without a prompt; password re-prompts; bare name edits the account.</summary>
    [Fact]
    public void Username_Existing_PrivilegePasswordAndEdit()
    {
        Enter("authentication local");
        var originalHash = (string)User("admin")!["PasswordHash"]!;

        Run("username admin privilege 12").Action.Should().Be(ConfigurationLineAction.Continue);
        ((string?)User("admin")!["PrivilegeLevel"]).Should().Be("12");
        ((string)User("admin")!["PasswordHash"]!).Should().Be(originalHash);

        var change = Run("username admin password");
        change.Action.Should().Be(ConfigurationLineAction.PromptSecret);
        change.Secret!.CancelledMessage.Should().Be("% Cancelled. Value not changed.");
        _processor.ApplySecret(change.Secret, "Changed-Pass-9", "Changed-Pass-9").Should().BeNull();
        new OperatorPasswordHasher().VerifyPassword("admin", (string)User("admin")!["PasswordHash"]!, "Changed-Pass-9").Should().BeTrue();

        Run("username admin").Output.Should().BeNullOrEmpty();
        _edit.Mode.Should().Be("config-user");
        Run("exit");
        _edit.Mode.Should().Be("config-auth-local");
        Run("username newop password").Output.Should().Contain("does not exist");
    }

    /// <summary>Passwords typed on the command line are rejected, never echoed, and dropped from history.</summary>
    [Theory]
    [InlineData("username admin password Inline-Pass-1")]
    [InlineData("username newop password Inline-Pass-1")]
    [InlineData("username newop privilege 15 secret Inline-Pass-1")]
    [InlineData("username newop privilege 15 password Inline-Pass-1")]
    [InlineData("enable-secret-level 5 Inline-Pass-1")]
    public void InlinePasswords_RejectedAndForgotten(string line)
    {
        Enter("authentication local");
        var before = _edit.Candidate.ToJsonString();

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.Continue);
        result.ForgetHistory.Should().BeTrue();
        result.Output.Should().NotContain("Inline-Pass-1");
        _edit.Candidate.ToJsonString().Should().Be(before);
    }

    /// <summary><c>no username</c> removes an account; removing a missing one is reported.</summary>
    [Fact]
    public void NoUsername_RemovesAccount()
    {
        Enter("authentication local");

        Run("no username admin").Output.Should().BeNullOrEmpty();
        User("admin").Should().BeNull();
        Run("no username ghost").Output.Should().Contain("does not exist");
        Run("default username admin").Output.Should().Contain("% Invalid input detected");
    }

    /// <summary>
    /// <c>enable-secret-level &lt;n&gt;</c> prompts and stores a hash; the hash form validates;
    /// the entry exists only after a secret is accepted.
    /// </summary>
    [Fact]
    public void EnableSecretLevel_PromptAndHashForms()
    {
        Enter("authentication local");
        var node = ConfigPaths.Node(_edit.Candidate, "authentication local");
        var hasher = new OperatorPasswordHasher();

        var prompt = Run("enable-secret-level 5");
        prompt.Action.Should().Be(ConfigurationLineAction.PromptSecret);
        prompt.Secret!.Prompt.Should().Be("Enter secret: ");
        (node["EnableSecrets"] as JsonArray ?? new JsonArray()).Should().BeEmpty();
        _processor.ApplySecret(prompt.Secret, "Level-5-Secret", "Level-5-Secret").Should().BeNull();
        var entry = (JsonObject)((JsonArray)node["EnableSecrets"]!).Single()!;
        hasher.VerifyEnableSecret((string)entry["SecretHash"]!, "Level-5-Secret").Should().BeTrue();
        _edit.Mode.Should().Be("config-auth-local", "the entry has no sub-mode to wander into");

        var bad = Run("enable-secret-level 5 hash not-a-hash");
        bad.ForgetHistory.Should().BeTrue();
        bad.Output.Should().Contain("Identity V3 hash is required");

        var other = hasher.HashEnableSecret("Other-Secret");
        var good = Run("enable-secret-level 7 hash " + other);
        good.ForgetHistory.Should().BeTrue();
        good.Output.Should().BeNullOrEmpty();
        ((JsonArray)node["EnableSecrets"]!).Should().HaveCount(2);

        Run("enable-secret-level 16").Output.Should().Contain("% Invalid value");
        Run("no enable-secret-level 7").Output.Should().BeNullOrEmpty();
        ((JsonArray)node["EnableSecrets"]!).Should().ContainSingle();
    }

    /// <summary>
    /// Every collection is served by a grammar that can execute: one-line collections (no sub-mode) hold exactly
    /// one hashed secret; collections with a sub-mode are operator-account collections. No unreachable mode exists.
    /// </summary>
    [Fact]
    public void Schema_CollectionsMatchTheGrammarsThatServeThem()
    {
        var sections = new List<ConfigSection>();
        void Collect(ConfigSection section)
        {
            sections.Add(section);
            section.Children.ToList().ForEach(Collect);
        }

        Collect(ConfigurationSchema.Root);
        var collections = sections.SelectMany(s => s.Collections).ToList();

        collections.Should().NotBeEmpty();
        foreach (var collection in collections.Where(c => c.Mode is null))
        {
            collection.Settings.Should().ContainSingle()
                .Which.Kind.Should().BeOneOf(ConfigValueKind.EnableSecretHash, ConfigValueKind.PasswordHash);
        }

        foreach (var collection in collections.Where(c => c.Mode is not null))
        {
            collection.KeyKind.Should().Be(ConfigValueKind.Text);
            collection.Settings.Select(s => s.Kind).Should().Contain([ConfigValueKind.PasswordHash, ConfigValueKind.PrivilegeLevel]);
        }

        var modes = sections.Select(s => s.Mode).Concat(collections.Select(c => c.Mode)).OfType<string>().ToList();
        modes.Should().NotContain("config-enable");
        collections.Single(c => c.Keyword == "enable-secret-level").Mode.Should().BeNull();
    }

    /// <summary><c>enable-secret-level</c> help and completion in (config-auth-local); no sub-mode command is offered.</summary>
    [Fact]
    public void EnableSecretLevel_HelpAndCompletion()
    {
        Enter("authentication local");
        var hash = new OperatorPasswordHasher().HashEnableSecret("Help-Check");

        HelpTokens(string.Empty).Should().Contain(["enable-secret", "enable-secret-level"]);
        Run("help").Output.Should().Contain("enable-secret-level <1-15> [hash <hash>]");
        HelpTokens("enable-secret-level ").Should().Equal("<1-15>");
        HelpTokens("enable-secret-level 5 ").Should().Equal("<cr>", "hash");
        HelpTokens("enable-secret-level 5 hash ").Should().Equal("<hash>");
        HelpTokens("enable-secret-level 5 hash " + hash + " ").Should().Equal("<cr>");
        HelpTokens("enable-secret-level 5 secret ").Should().BeEmpty();
        HelpTokens("enable-secret-level 16 ").Should().BeEmpty();
        _processor.Complete(_edit, "enable-secret-l", 15, 80).CompletionText.Should().Be("enable-secret-level");
        _processor.Complete(_edit, "enable-secret-level 5 h", 23, 80).CompletionText.Should().Be("hash");
        _edit.Mode.Should().Be("config-auth-local");
    }

    /// <summary>Incomplete, invalid, and old sub-mode forms are rejected without changing the candidate.</summary>
    [Theory]
    [InlineData("enable-secret-level", "% Incomplete command.", false)]
    [InlineData("enable-secret-level 0", "% Invalid value. Range is 1-15.", false)]
    [InlineData("enable-secret-level 16", "% Invalid value. Range is 1-15.", false)]
    [InlineData("enable-secret-level five", "% Invalid value. Range is 1-15.", false)]
    [InlineData("enable-secret-level 5 hash", "% Incomplete command.", true)]
    [InlineData("enable-secret-level 5 hash a b", "% Invalid input detected at '^' marker.", true)]
    [InlineData("enable-secret-level 5 secret", "secrets are not accepted on the command line", true)]
    [InlineData("enable-secret-level 5 secret Old-Form-1", "secrets are not accepted on the command line", true)]
    [InlineData("secret", "% Invalid input detected at '^' marker.", false)]
    [InlineData("default enable-secret-level 5", "% Invalid input detected at '^' marker.", false)]
    public void EnableSecretLevel_RejectsIncompleteAndInvalidInput(string line, string expected, bool forget)
    {
        Enter("authentication local");
        var before = _edit.Candidate.ToJsonString();

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.Continue);
        result.Output.Should().Contain(expected).And.NotContain("Old-Form-1");
        result.ForgetHistory.Should().Be(forget);
        _edit.Candidate.ToJsonString().Should().Be(before);
        _edit.Mode.Should().Be("config-auth-local");
    }

    /// <summary>
    /// Re-entering an existing level replaces its hash in place; cancellation and empty input name the right outcome.
    /// </summary>
    [Fact]
    public void EnableSecretLevel_ReplaceCancelAndEmpty()
    {
        Enter("authentication local");
        var node = ConfigPaths.Node(_edit.Candidate, "authentication local");
        var hasher = new OperatorPasswordHasher();
        _processor.ApplySecret(Run("enable-secret-level 5").Secret!, "First-Secret-5", "First-Secret-5").Should().BeNull();

        Run("enable-secret-level 6").Secret!.CancelledMessage.Should().Be("% Cancelled. 'enable-secret-level 6' was not created.");
        var replace = Run("enable-secret-level 5").Secret!;
        replace.CancelledMessage.Should().Be("% Cancelled. Value not changed.");
        _processor.ApplySecret(replace, string.Empty, string.Empty).Should().Be("% An empty value is not accepted. Use 'no enable-secret-level 5' to remove it.");
        _processor.ApplySecret(replace, "Second-Secret-5", "Second-Secret-5").Should().BeNull();

        var entry = (JsonObject)((JsonArray)node["EnableSecrets"]!).Single()!;
        ((string?)entry["PrivilegeLevel"]).Should().Be("5");
        hasher.VerifyEnableSecret((string)entry["SecretHash"]!, "Second-Secret-5").Should().BeTrue();
        hasher.VerifyEnableSecret((string)entry["SecretHash"]!, "First-Secret-5").Should().BeFalse();
    }

    /// <summary>
    /// Views and diffs show a level as one masked line (the command that sets it), never a sub-mode block or the hash.
    /// </summary>
    [Fact]
    public async Task EnableSecretLevel_ViewsAreOneMaskedLine()
    {
        Enter("authentication local");
        _processor.ApplySecret(Run("enable-secret-level 5").Secret!, "View-Secret-5", "View-Secret-5");
        var hash = (string)((JsonArray)ConfigPaths.Node(_edit.Candidate, "authentication local")["EnableSecrets"]!)[0]!["SecretHash"]!;

        var candidate = Run("show candidate-config").Output!;
        var changes = Run("show configuration changes").Output!;

        candidate.Should().Contain("enable-secret-level 5 <hidden>").And.NotContain(" secret <hidden>").And.NotContain(hash);
        changes.Should().Contain("* authentication local enable-secret-level 5 <set>").And.NotContain("5 secret").And.NotContain(hash);

        (await _service.WriteAsync(_edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);
        Run("no enable-secret-level 5");
        Run("show configuration changes").Output.Should().Contain("* authentication local enable-secret-level 5 <removed>");
        _service.RenderStartup().Should().Contain("enable-secret-level 5 <hidden>").And.NotContain(hash);
    }

    /// <summary>A level is private to the candidate until <c>write</c>, then persisted only as a validated hash.</summary>
    [Fact]
    public async Task EnableSecretLevel_IsolatedUntilWrite_ThenPersistedAsHash()
    {
        Enter("authentication local");
        _processor.ApplySecret(Run("enable-secret-level 9").Secret!, "Persist-Secret-9", "Persist-Secret-9");
        var other = _service.BeginEdit(ConfigurationFixture.Admin("bob"), out _);

        _fixture.ReadFile().Should().NotContain("EnableSecrets");
        ((ConfigPaths.Node(other.Candidate, "authentication local")["EnableSecrets"] as JsonArray)?.Count ?? 0).Should().Be(0);

        var result = await _service.WriteAsync(_edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        result.RestartRequired.Should().Contain("authentication local enable-secret-level 9");
        var file = _fixture.ReadFile();
        file.Should().NotContain("Persist-Secret-9");
        var stored = JsonNode.Parse(file)!["TechConnectServer"]!["Authentication"]!["Local"]!["EnableSecrets"]!.AsArray().Single()!;
        ((string?)stored["PrivilegeLevel"]).Should().Be("9");
        new OperatorPasswordHasher().VerifyEnableSecret((string)stored["SecretHash"]!, "Persist-Secret-9").Should().BeTrue();
    }

    /// <summary>Help, completion, and command output never reveal a stored hash or a password.</summary>
    [Fact]
    public void Secrets_NeverAppearInHelpCompletionOrOutput()
    {
        Enter("authentication local");
        var create = Run("username newop privilege 5");
        _processor.ApplySecret(create.Secret!, "Leak-Check-77", "Leak-Check-77");
        var hash = (string)User("newop")!["PasswordHash"]!;
        var adminHash = (string)User("admin")!["PasswordHash"]!;

        var texts = new List<string?>();
        foreach (var line in new[] { "", "username ", "username newop ", "username newop password ", "username newop privilege ", "no username ", "do show ", "show " })
        {
            texts.Add(_processor.Help(_edit, line, line.Length, 80).DisplayText);
            texts.Add(_processor.Complete(_edit, line + "n", line.Length + 1, 80).DisplayText);
        }

        texts.Add(Run("show candidate-config").Output);
        texts.Add(Run("show configuration changes").Output);
        texts.Add(Run("do show candidate-config").Output);
        Run("username newop");
        texts.Add(_processor.Help(_edit, "password ", 9, 80).DisplayText);

        var all = string.Join("\n", texts.Where(t => t is not null));
        all.Should().NotContain("Leak-Check-77").And.NotContain(hash).And.NotContain(adminHash);
    }

    /// <summary><c>do show</c> runs configuration-session views here and hands EXEC show commands to the dispatcher.</summary>
    [Theory]
    [InlineData("do show running-config", "show running-config")]
    [InlineData("do sh run", "show running-config")]
    [InlineData("do show startup-config", "show startup-config")]
    [InlineData("do show users", "show users")]
    [InlineData("do show history user admin", "show history user admin")]
    public void Do_ShowExecCommand_IsHandedToDispatcher(string line, string command)
    {
        Enter("authentication local");

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.ExecuteExec);
        result.ExecCommand.Should().Be(command);
        _edit.Mode.Should().Be("config-auth-local");
    }

    /// <summary><c>do show configuration changes</c> and <c>do show candidate-config</c> run locally.</summary>
    [Fact]
    public void Do_ShowSessionViews_RunLocally()
    {
        Enter("ssh");
        Run("port 2301");

        var changes = Run("do show configuration changes");
        var candidate = Run("do show candidate-config");

        changes.Action.Should().Be(ConfigurationLineAction.Continue);
        changes.Output.Should().Contain("ssh port");
        candidate.Output.Should().Contain("port 2301");
    }

    /// <summary><c>do</c> accepts nothing but read-only show commands, and never changes the candidate.</summary>
    [Theory]
    [InlineData("do write")]
    [InlineData("do write memory")]
    [InlineData("do configure terminal")]
    [InlineData("do enable 15")]
    [InlineData("do disable")]
    [InlineData("do copy https://h/x serial:abc")]
    [InlineData("do ping 127.0.0.1")]
    [InlineData("do disconnect abc")]
    [InlineData("do exit")]
    [InlineData("do port 2300")]
    public void Do_RejectsNonShowCommands(string line)
    {
        Enter("ssh");
        var before = _edit.Candidate.ToJsonString();

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.Continue);
        result.Output.Should().Contain("% Invalid input detected at '^' marker.").And.Contain("'do' runs read-only EXEC 'show' commands only");
        _edit.Candidate.ToJsonString().Should().Be(before);
        _edit.Mode.Should().Be("config-ssh");
    }

    /// <summary>Malformed <c>do</c> lines get the usual categories.</summary>
    [Fact]
    public void Do_MalformedLines()
    {
        Run("do").Output.Should().Be("% Incomplete command.");
        Run("do show").Output.Should().Be("% Incomplete command.");
        Run("do show bogus").Output.Should().Contain("% Invalid input detected at '^' marker.");
        Run("do show c").Output.Should().StartWith("% Ambiguous command:");
        HelpTokens("do ").Should().Equal("show");
        HelpTokens("do show ").Should().Contain(["users", "running-config", "startup-config", "candidate-config", "configuration"]);
    }

    /// <summary><c>do</c> is re-authorized like every configuration line: below 15 it leaves with a denial.</summary>
    [Fact]
    public void Do_BelowLevel15_IsDenied()
    {
        var low = TestAuthenticatedOperator.Create("low", PrivilegeLevel.FromInt(14));

        var result = _processor.Execute(_edit, low, "do show users");

        result.Action.Should().Be(ConfigurationLineAction.Leave);
        result.Output.Should().Contain("Permission denied");
        result.ExecCommand.Should().BeNull();
    }

    /// <summary>A created account is persisted only by write, as a hash, and survives a reload.</summary>
    [Fact]
    public async Task Username_Create_PersistsOnlyOnWrite_AsHash()
    {
        Enter("authentication local");
        _processor.ApplySecret(Run("username newop privilege 7").Secret!, "Persist-Pass-3", "Persist-Pass-3");
        _fixture.ReadFile().Should().NotContain("newop");

        var result = await _service.WriteAsync(_edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        var file = _fixture.ReadFile();
        file.Should().Contain("newop").And.NotContain("Persist-Pass-3");
        var users = JsonNode.Parse(file)!["TechConnectServer"]!["Authentication"]!["Local"]!["LocalUsers"]!.AsArray();
        var stored = users.Single(u => (string?)u!["Username"] == "newop")!;
        ((string?)stored["PrivilegeLevel"]).Should().Be("7");
        new OperatorPasswordHasher().VerifyPassword("newop", (string)stored["PasswordHash"]!, "Persist-Pass-3").Should().BeTrue();
    }
}

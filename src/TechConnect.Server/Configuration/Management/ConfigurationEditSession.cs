// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConfigurationEditSession.cs
//
// Purpose:
// One operator's private candidate configuration and configuration-mode
// context stack. Never shared between operators or persisted implicitly.
//
// -----------------------------------------------------------------------------

using System.Text.Json.Nodes;

namespace TechConnect.Server.Configuration.Management;

/// <summary>
/// One configuration-mode context (global, section, or collection entry).
/// </summary>
/// <param name="Mode">Prompt mode, for example <c>config-ssh</c>.</param>
/// <param name="Path">CLI path of the context (empty for global).</param>
/// <param name="Section">Section schema when the frame is a section.</param>
/// <param name="Collection">Collection schema when the frame is a collection entry.</param>
/// <param name="Node">Candidate JSON object edited by this context.</param>
internal sealed record ConfigurationFrame(
    string Mode,
    string Path,
    ConfigSection? Section,
    ConfigCollection? Collection,
    JsonObject Node)
{
    /// <summary>Settings editable in this context.</summary>
    public IReadOnlyList<ConfigSetting> Settings => Collection?.Settings ?? Section!.Settings;

    /// <summary>Nested sections reachable from this context.</summary>
    public IReadOnlyList<ConfigSection> Children => Section?.Children ?? [];

    /// <summary>Keyed collections reachable from this context.</summary>
    public IReadOnlyList<ConfigCollection> Collections => Section?.Collections ?? [];
}

/// <summary>
/// Private candidate configuration for one operator's configuration session.
/// </summary>
/// <remarks>
/// Created from a deep copy of the effective configuration; edits mutate only
/// <see cref="Candidate"/>. Not thread-safe — owned by a single CLI session loop.
/// </remarks>
internal sealed class ConfigurationEditSession
{
    /// <summary>Context stack; index 0 is the global context.</summary>
    private readonly List<ConfigurationFrame> _frames = [];

    /// <summary>
    /// Creates an edit session.
    /// </summary>
    /// <param name="operatorUsername">Owning operator (logging only).</param>
    /// <param name="operatorSessionId">Owning operator session id.</param>
    /// <param name="effective">Effective configuration snapshot (copied).</param>
    /// <param name="startupFileHash">Hash of appsettings.json when the session began.</param>
    public ConfigurationEditSession(
        string operatorUsername,
        string operatorSessionId,
        JsonObject effective,
        string startupFileHash)
    {
        ArgumentNullException.ThrowIfNull(effective);
        OperatorUsername = operatorUsername ?? throw new ArgumentNullException(nameof(operatorUsername));
        OperatorSessionId = operatorSessionId ?? throw new ArgumentNullException(nameof(operatorSessionId));
        Baseline = (JsonObject)effective.DeepClone();
        Candidate = (JsonObject)effective.DeepClone();
        StartupFileHash = startupFileHash;
        _frames.Add(new ConfigurationFrame("config", string.Empty, ConfigurationSchema.Root, null, Candidate));
    }

    /// <summary>Owning operator username.</summary>
    public string OperatorUsername { get; }

    /// <summary>Owning operator session id.</summary>
    public string OperatorSessionId { get; }

    /// <summary>Private candidate configuration (mutable).</summary>
    public JsonObject Candidate { get; }

    /// <summary>Configuration the candidate started from, or last successfully written.</summary>
    public JsonObject Baseline { get; private set; }

    /// <summary>SHA-256 of appsettings.json when this session began or last wrote it.</summary>
    public string StartupFileHash { get; private set; }

    /// <summary>Active context.</summary>
    public ConfigurationFrame Current => _frames[^1];

    /// <summary>Context depth (1 = global).</summary>
    public int Depth => _frames.Count;

    /// <summary>Prompt mode of the active context.</summary>
    public string Mode => Current.Mode;

    /// <summary>Whether the candidate differs from <see cref="Baseline"/>.</summary>
    public bool HasUnsavedChanges => !JsonNode.DeepEquals(Candidate, Baseline);

    /// <summary>Number of changed setting paths (no values).</summary>
    public int UnsavedChangeCount => ConfigurationModel.ChangedEntries(Baseline, Candidate).Count;

    /// <summary>Enters a nested context.</summary>
    public void Push(ConfigurationFrame frame) => _frames.Add(frame ?? throw new ArgumentNullException(nameof(frame)));

    /// <summary>Leaves the active context.</summary>
    /// <returns><see langword="false"/> when already at the global context.</returns>
    public bool Pop()
    {
        if (_frames.Count <= 1)
        {
            return false;
        }

        _frames.RemoveAt(_frames.Count - 1);
        return true;
    }

    /// <summary>
    /// Records a successful write: the candidate becomes the new baseline.
    /// </summary>
    /// <param name="newFileHash">Hash of the file just written.</param>
    public void MarkSaved(string newFileHash)
    {
        Baseline = (JsonObject)Candidate.DeepClone();
        StartupFileHash = newFileHash;
    }
}

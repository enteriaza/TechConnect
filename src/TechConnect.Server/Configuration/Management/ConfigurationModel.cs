// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConfigurationModel.cs
//
// Purpose:
// Schema-driven helpers over the JSON configuration model: read from
// IConfiguration, typed value parsing, masked Cisco-style rendering,
// flattening for diffs, and merging into the appsettings.json document.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Configuration.Management;

/// <summary>
/// One flattened setting value used for display and change detection.
/// </summary>
/// <param name="Path">CLI-style path, for example <c>ssh port</c>.</param>
/// <param name="Comparable">Exact JSON used for equality (never displayed).</param>
/// <param name="Display">Masked, operator-safe display text.</param>
/// <param name="IsSecret">Whether the value is sensitive.</param>
/// <param name="ApplyMode">Runtime vs restart-required.</param>
internal sealed record ConfigEntry(
    string Path,
    string Comparable,
    string Display,
    bool IsSecret,
    ConfigApplyMode ApplyMode);

/// <summary>
/// Schema-driven operations over the managed configuration JSON model.
/// </summary>
/// <remarks>
/// The model is a <see cref="JsonObject"/> shaped like <c>appsettings.json</c> but containing
/// only the settings in <see cref="ConfigurationSchema"/>. Secret values are never rendered.
/// </remarks>
internal static partial class ConfigurationModel
{
    /// <summary>Masked placeholder shown for configured secrets.</summary>
    public const string HiddenSecret = "<hidden>";

    /// <summary>Serializer options for appsettings.json (indented, readable hashes).</summary>
    public static readonly JsonSerializerOptions FileWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Parse options for appsettings.json (comments/trailing commas tolerated like the config provider).</summary>
    public static readonly JsonDocumentOptions FileParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Case-insensitive node options (configuration keys are case-insensitive).</summary>
    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the managed settings from <paramref name="configuration"/> (missing keys take schema defaults).
    /// </summary>
    /// <param name="configuration">Effective configuration (all providers).</param>
    /// <returns>New model instance.</returns>
    public static JsonObject Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = new JsonObject(NodeOptions);
        ReadSection(configuration, string.Empty, ConfigurationSchema.Root, root);
        return root;
    }

    /// <summary>
    /// Reads the managed settings from raw appsettings JSON bytes (empty/missing = defaults).
    /// </summary>
    /// <param name="json">File content, or <see langword="null"/> when the file does not exist.</param>
    /// <returns>New model instance.</returns>
    /// <exception cref="InvalidDataException">The content is not valid JSON.</exception>
    public static JsonObject ReadJson(byte[]? json)
    {
        var builder = new ConfigurationBuilder();
        if (json is { Length: > 0 })
        {
            try
            {
                builder.AddJsonStream(new MemoryStream(json, writable: false));
                return Read(builder.Build());
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
            {
                throw new InvalidDataException("The configuration file is not valid JSON.", ex);
            }
        }

        return Read(builder.Build());
    }

    /// <summary>Recursively reads one section into <paramref name="target"/>.</summary>
    private static void ReadSection(IConfiguration configuration, string prefix, ConfigSection section, JsonObject target)
    {
        var path = ConfigurationSchema.Combine(prefix, string.Join(':', section.JsonPath));
        foreach (var setting in section.Settings)
        {
            SetValue(target, setting, ReadSetting(configuration, ConfigurationSchema.Combine(path, setting.JsonName), setting));
        }

        foreach (var collection in section.Collections)
        {
            var array = new JsonArray();
            foreach (var child in configuration.GetSection(ConfigurationSchema.Combine(path, collection.JsonName)).GetChildren())
            {
                var item = new JsonObject(NodeOptions)
                {
                    [collection.KeyJsonName] = JsonValue.Create(child[collection.KeyJsonName] ?? string.Empty)
                };
                foreach (var setting in collection.Settings)
                {
                    SetValue(item, setting, ReadSetting(child, setting.JsonName, setting));
                }

                array.Add(item);
            }

            target[collection.JsonName] = array;
        }

        foreach (var child in section.Children)
        {
            ReadSection(configuration, path, child, ObjectAt(target, child.JsonPath, create: true)!);
        }
    }

    /// <summary>Reads one setting value at <paramref name="key"/>.</summary>
    private static JsonNode? ReadSetting(IConfiguration configuration, string key, ConfigSetting setting)
    {
        var section = configuration.GetSection(key);
        switch (setting.Shape)
        {
            case ConfigShape.List:
            {
                var children = section.GetChildren().ToList();
                if (children.Count == 0 && section.Value is null)
                {
                    return setting.Default();
                }

                var array = new JsonArray();
                foreach (var child in children)
                {
                    array.Add(JsonValue.Create(child.Value ?? string.Empty));
                }

                return array;
            }

            case ConfigShape.Map:
            {
                var children = section.GetChildren().ToList();
                if (children.Count == 0)
                {
                    return setting.Default();
                }

                var map = new JsonObject(NodeOptions);
                foreach (var child in children)
                {
                    map[child.Key] = JsonValue.Create(child.Value ?? string.Empty);
                }

                return map;
            }
        }

        var raw = section.Value;
        if (raw is null)
        {
            return section.GetChildren().Any() ? JsonValue.Create(string.Empty) : setting.Default();
        }

        if (setting.Nullable && raw.Length == 0)
        {
            return null;
        }

        return setting.Kind switch
        {
            ConfigValueKind.Boolean when bool.TryParse(raw, out var b) => JsonValue.Create(b),
            ConfigValueKind.Integer when long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) =>
                JsonValue.Create(n),
            _ => JsonValue.Create(raw)
        };
    }

    /// <summary>
    /// Navigates (optionally creating) nested objects below <paramref name="parent"/>.
    /// </summary>
    /// <returns>The object, or <see langword="null"/> when missing and <paramref name="create"/> is false.</returns>
    public static JsonObject? ObjectAt(JsonObject parent, IEnumerable<string> segments, bool create)
    {
        var current = parent;
        foreach (var segment in segments)
        {
            if (current[segment] is JsonObject existing)
            {
                current = existing;
                continue;
            }

            if (!create)
            {
                return null;
            }

            var created = new JsonObject(NodeOptions);
            current[segment] = created;
            current = created;
        }

        return current;
    }

    /// <summary>Gets the raw value of <paramref name="setting"/> within <paramref name="container"/>.</summary>
    public static JsonNode? GetValue(JsonObject container, ConfigSetting setting)
    {
        var segments = setting.JsonName.Split(':');
        var parent = ObjectAt(container, segments[..^1], create: false);
        return parent?[segments[^1]];
    }

    /// <summary>Sets the raw value of <paramref name="setting"/> within <paramref name="container"/>.</summary>
    public static void SetValue(JsonObject container, ConfigSetting setting, JsonNode? value)
    {
        var segments = setting.JsonName.Split(':');
        var parent = ObjectAt(container, segments[..^1], create: true)!;
        parent[segments[^1]] = value?.DeepClone();
    }

    /// <summary>Returns the string value of a scalar node, or <see langword="null"/>.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Returns the items of a list node as strings.</summary>
    public static List<string> AsList(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : n?.ToJsonString() ?? string.Empty).ToList()
            : [];

    /// <summary>Finds a collection entry by key (usernames are case-insensitive).</summary>
    public static JsonObject? FindItem(JsonArray array, ConfigCollection collection, string key)
    {
        foreach (var node in array)
        {
            if (node is JsonObject item && KeyEquals(collection, AsString(item[collection.KeyJsonName]), key))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>Key comparison for a collection.</summary>
    public static bool KeyEquals(ConfigCollection collection, string? left, string? right) =>
        collection.KeyKind == ConfigValueKind.Text
            ? OperatorUsernameRules.Comparer.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty)
            : string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Parses operator input for a scalar (or list item) value of <paramref name="setting"/>.
    /// </summary>
    /// <param name="setting">Target setting (non-secret kinds only).</param>
    /// <param name="raw">Operator text.</param>
    /// <param name="value">Parsed JSON value.</param>
    /// <param name="error">Cisco-style error when parsing fails.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryParseValue(ConfigSetting setting, string raw, out JsonNode? value, out string? error)
    {
        value = null;
        error = null;
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            error = "% Incomplete command";
            return false;
        }

        if (raw.Length > setting.MaxLength || raw.Any(char.IsControl))
        {
            error = "% Invalid value";
            return false;
        }

        switch (setting.Kind)
        {
            case ConfigValueKind.Boolean:
                if (TryParseBoolean(raw, out var flag))
                {
                    value = JsonValue.Create(flag);
                    return true;
                }

                error = "% Invalid value. Use true, false, enable, or disable.";
                return false;

            case ConfigValueKind.Integer:
                if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    error = "% Invalid value. An integer is required.";
                    return false;
                }

                if (number < setting.Minimum || number > setting.Maximum)
                {
                    error = $"% Invalid value. Range is {setting.Minimum}-{setting.Maximum}.";
                    return false;
                }

                value = JsonValue.Create(number);
                return true;

            case ConfigValueKind.IpAddress:
                if (TryParseIpLiteral(raw, out var address))
                {
                    value = JsonValue.Create(address!.ToString());
                    return true;
                }

                error = "% Invalid value. An IPv4 or IPv6 address is required.";
                return false;

            case ConfigValueKind.Host:
                if (TryParseIpLiteral(raw, out var hostAddress))
                {
                    value = JsonValue.Create(hostAddress!.ToString());
                    return true;
                }

                if (raw.Length <= 253 && HostNameRegex().IsMatch(raw))
                {
                    value = JsonValue.Create(raw);
                    return true;
                }

                error = "% Invalid value. A host name or IP address is required.";
                return false;

            case ConfigValueKind.Enum:
            {
                var match = setting.AllowedValues.FirstOrDefault(v => v.Equals(raw, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    error = "% Invalid value. Use one of: " + string.Join(", ", setting.AllowedValues) + ".";
                    return false;
                }

                value = JsonValue.Create(match);
                return true;
            }

            case ConfigValueKind.Thumbprint:
            {
                var normalized = raw.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
                if (normalized.Length is not (40 or 64) || !normalized.All(char.IsAsciiHexDigit))
                {
                    error = "% Invalid value. A 40- or 64-character hex thumbprint is required.";
                    return false;
                }

                value = JsonValue.Create(normalized.ToUpperInvariant());
                return true;
            }

            case ConfigValueKind.PrivilegeLevel:
                if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var level) &&
                    PrivilegeLevel.IsValid(level) &&
                    raw == level.ToString(CultureInfo.InvariantCulture))
                {
                    value = JsonValue.Create(raw);
                    return true;
                }

                error = $"% Invalid value. Range is {PrivilegeLevel.Minimum}-{PrivilegeLevel.Maximum}.";
                return false;

            case ConfigValueKind.DirectoryPath:
                if (!IsSafeDirectory(raw))
                {
                    error = "% Invalid value. The directory path contains invalid characters.";
                    return false;
                }

                value = JsonValue.Create(raw);
                return true;

            case ConfigValueKind.FileNamePrefix:
                if (!IsSafeFileNamePrefix(raw))
                {
                    error = "% Invalid value. Use a file name prefix without path separators or invalid characters.";
                    return false;
                }

                value = JsonValue.Create(raw);
                return true;

            case ConfigValueKind.PublicKeyLine:
                try
                {
                    SshKeyMaterial.ParsePublicKeyLine(raw, "authorized-public-keys");
                }
                catch (InvalidOperationException)
                {
                    error = "% Invalid value. An OpenSSH public key line (for example 'ssh-ed25519 AAAA... comment') is required.";
                    return false;
                }

                value = JsonValue.Create(raw);
                return true;

            case ConfigValueKind.Text:
                value = JsonValue.Create(raw);
                return true;

            default:
                error = "% Invalid input detected";
                return false;
        }
    }

    /// <summary>Parses <c>true|false|enable|disable</c> (case-insensitive).</summary>
    public static bool TryParseBoolean(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true":
            case "enable":
                value = true;
                return true;
            case "false":
            case "disable":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>Parses a strict IPv4 dotted-quad or IPv6 literal (rejects shorthand such as <c>1</c>).</summary>
    public static bool TryParseIpLiteral(string raw, out IPAddress? address)
    {
        address = null;
        if (raw.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(raw, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return Ipv4Regex().IsMatch(raw) &&
               IPAddress.TryParse(raw, out address) &&
               address.AddressFamily == AddressFamily.InterNetwork;
    }

    /// <summary>Whether a log directory value is safe (no control/invalid path characters or wildcards).</summary>
    public static bool IsSafeDirectory(string value) =>
        value.Length <= 260 &&
        !value.Any(char.IsControl) &&
        value.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        value.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0;

    /// <summary>Whether a file-name prefix cannot escape the log directory.</summary>
    public static bool IsSafeFileNamePrefix(string value) =>
        value.Length is > 0 and <= 100 &&
        value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;

    /// <summary>
    /// Validates stored value types and safety rules not covered by the startup options validators.
    /// </summary>
    /// <param name="model">Model to check.</param>
    /// <param name="failures">Receives operator-safe failure messages (never secret values).</param>
    public static void ValidateValues(JsonObject model, List<string> failures)
    {
        foreach (var (path, setting, node) in EnumerateValues(model))
        {
            var problem = ValidateStoredValue(setting, node);
            if (problem is not null)
            {
                failures.Add($"{path}: {problem}");
            }
        }
    }

    /// <summary>Returns a problem description for a stored value, or <see langword="null"/> when acceptable.</summary>
    private static string? ValidateStoredValue(ConfigSetting setting, JsonNode? node)
    {
        if (setting.Shape == ConfigShape.List)
        {
            if (node is not JsonArray array)
            {
                return "a list is required";
            }

            foreach (var item in array)
            {
                if (AsString(item) is not { } text)
                {
                    return "list entries must be text";
                }

                if (setting.Kind == ConfigValueKind.Enum &&
                    !setting.AllowedValues.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    return "unsupported value; use one of: " + string.Join(", ", setting.AllowedValues);
                }
            }

            return null;
        }

        if (setting.Shape == ConfigShape.Map)
        {
            if (node is not JsonObject map)
            {
                return "a key/value map is required";
            }

            foreach (var pair in map)
            {
                if (!LogSourceRegex().IsMatch(pair.Key))
                {
                    return "invalid source name";
                }

                if (AsString(pair.Value) is not { } text ||
                    !setting.AllowedValues.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    return "unsupported value; use one of: " + string.Join(", ", setting.AllowedValues);
                }
            }

            return null;
        }

        if (node is null)
        {
            return setting.Nullable ? null : "a value is required";
        }

        if (setting.Kind == ConfigValueKind.Boolean)
        {
            return node is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? null
                : "true or false is required";
        }

        if (setting.Kind == ConfigValueKind.Integer)
        {
            return node is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.TryGetValue<long>(out _)
                ? null
                : "an integer is required";
        }

        if (AsString(node) is not { } textValue)
        {
            return "text is required";
        }

        return setting.Kind switch
        {
            ConfigValueKind.Enum when !setting.AllowedValues.Contains(textValue, StringComparer.OrdinalIgnoreCase) =>
                "unsupported value; use one of: " + string.Join(", ", setting.AllowedValues),
            ConfigValueKind.DirectoryPath when textValue.Length > 0 && !IsSafeDirectory(textValue) =>
                "the directory path contains invalid characters",
            ConfigValueKind.FileNamePrefix when !IsSafeFileNamePrefix(textValue) =>
                "the file name prefix must not contain path separators or invalid characters",
            ConfigValueKind.HostPrivateKey when textValue.Length > 0 && !IsImportableHostKey(textValue) =>
                "the SSH host private key cannot be imported",
            _ => null
        };
    }

    /// <summary>Whether PEM text imports as a private host key (no details are surfaced).</summary>
    private static bool IsImportableHostKey(string pem)
    {
        try
        {
            SshKeyMaterial.ImportHostPrivateKey(pem);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Enumerates (path, setting, value) for every leaf, including collection entries.</summary>
    private static IEnumerable<(string Path, ConfigSetting Setting, JsonNode? Value)> EnumerateValues(JsonObject model)
    {
        return Walk(ConfigurationSchema.Root, model, string.Empty);

        static IEnumerable<(string, ConfigSetting, JsonNode?)> Walk(ConfigSection section, JsonObject node, string prefix)
        {
            foreach (var setting in section.Settings)
            {
                yield return (Join(prefix, setting.Keyword), setting, GetValue(node, setting));
            }

            foreach (var collection in section.Collections)
            {
                if (node[collection.JsonName] is not JsonArray array)
                {
                    continue;
                }

                foreach (var item in array.OfType<JsonObject>())
                {
                    var itemPath = Join(prefix, collection.Keyword + " " + AsString(item[collection.KeyJsonName]));
                    foreach (var setting in collection.Settings)
                    {
                        yield return (Join(itemPath, setting.Keyword), setting, GetValue(item, setting));
                    }
                }
            }

            foreach (var child in section.Children)
            {
                var childNode = ObjectAt(node, child.JsonPath, create: false) ?? new JsonObject();
                foreach (var entry in Walk(child, childNode, Join(prefix, child.Keyword)))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// Flattens a model into masked entries keyed by CLI path (for diffs and change detection).
    /// </summary>
    public static IReadOnlyDictionary<string, ConfigEntry> Flatten(JsonObject model)
    {
        var entries = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
        Walk(ConfigurationSchema.Root, model, string.Empty, ConfigApplyMode.RestartRequired);
        return entries;

        void Walk(ConfigSection section, JsonObject node, string prefix, ConfigApplyMode inherited)
        {
            foreach (var setting in section.Settings)
            {
                var path = Join(prefix, setting.Keyword);
                var value = GetValue(node, setting);
                if (setting.Shape == ConfigShape.Map)
                {
                    foreach (var pair in value as JsonObject ?? new JsonObject())
                    {
                        var key = Join(path, pair.Key);
                        entries[key] = new ConfigEntry(key, pair.Value?.ToJsonString() ?? "null", AsString(pair.Value) ?? string.Empty, false, setting.ApplyMode);
                    }

                    continue;
                }

                entries[path] = new ConfigEntry(path, value?.ToJsonString() ?? "null", DisplayValue(setting, value), setting.IsSecret, setting.ApplyMode);
            }

            foreach (var collection in section.Collections)
            {
                foreach (var item in (node[collection.JsonName] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var key = AsString(item[collection.KeyJsonName]) ?? string.Empty;
                    var itemPath = Join(prefix, collection.Keyword + " " + key);
                    entries[itemPath] = new ConfigEntry(itemPath, "entry", "(entry)", false, inherited);
                    foreach (var setting in collection.Settings)
                    {
                        var path = Join(itemPath, setting.Keyword);
                        var value = GetValue(item, setting);
                        entries[path] = new ConfigEntry(path, value?.ToJsonString() ?? "null", DisplayValue(setting, value), setting.IsSecret, setting.ApplyMode);
                    }
                }
            }

            foreach (var child in section.Children)
            {
                Walk(child, ObjectAt(node, child.JsonPath, create: false) ?? new JsonObject(), Join(prefix, child.Keyword), inherited);
            }
        }
    }

    /// <summary>Masked, single-line display for a setting value.</summary>
    public static string DisplayValue(ConfigSetting setting, JsonNode? value)
    {
        if (setting.IsSecret)
        {
            return string.IsNullOrEmpty(AsString(value)) ? "(not set)" : HiddenSecret;
        }

        return setting.Shape switch
        {
            ConfigShape.List => AsList(value) is { Count: > 0 } list ? string.Join(", ", list) : "(empty)",
            _ => value switch
            {
                null => "(not set)",
                JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>() is { Length: > 0 } s ? s : "(empty)",
                JsonValue v when v.GetValueKind() == JsonValueKind.True => "true",
                JsonValue v when v.GetValueKind() == JsonValueKind.False => "false",
                _ => value.ToJsonString()
            }
        };
    }

    /// <summary>
    /// Renders a model as Cisco-style configuration text with secrets masked.
    /// </summary>
    /// <param name="model">Model to render.</param>
    /// <param name="title">Header line, for example <c>Current configuration (candidate):</c>.</param>
    /// <returns>CRLF-separated text.</returns>
    public static string Render(JsonObject model, string title)
    {
        var sb = new StringBuilder();
        sb.Append(title).Append("\r\n!");
        RenderSection(sb, ConfigurationSchema.Root, model, depth: 0);
        sb.Append("\r\nend");
        return sb.ToString();
    }

    /// <summary>Renders one section's lines.</summary>
    private static void RenderSection(StringBuilder sb, ConfigSection section, JsonObject node, int depth)
    {
        var indent = new string(' ', depth);
        foreach (var setting in section.Settings)
        {
            RenderSetting(sb, indent, setting, GetValue(node, setting));
        }

        foreach (var collection in section.Collections)
        {
            foreach (var item in (node[collection.JsonName] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                sb.Append("\r\n").Append(indent).Append(collection.Keyword).Append(' ')
                    .Append(AsString(item[collection.KeyJsonName]));
                foreach (var setting in collection.Settings)
                {
                    RenderSetting(sb, indent + " ", setting, GetValue(item, setting));
                }

                sb.Append("\r\n").Append(indent).Append(" exit");
            }
        }

        foreach (var child in section.Children)
        {
            sb.Append("\r\n").Append(indent).Append(child.Keyword);
            RenderSection(sb, child, ObjectAt(node, child.JsonPath, create: false) ?? new JsonObject(), depth + 1);
            if (depth == 0)
            {
                sb.Append("\r\n!");
            }
            else
            {
                sb.Append("\r\n").Append(indent).Append(" exit");
            }
        }
    }

    /// <summary>Renders one setting (masked when secret).</summary>
    private static void RenderSetting(StringBuilder sb, string indent, ConfigSetting setting, JsonNode? value)
    {
        void Line(string text) => sb.Append("\r\n").Append(indent).Append(text);

        if (setting.IsSecret)
        {
            Line(string.IsNullOrEmpty(AsString(value)) ? "no " + setting.Keyword : setting.Keyword + " " + HiddenSecret);
            return;
        }

        switch (setting.Shape)
        {
            case ConfigShape.List:
            {
                var items = AsList(value);
                if (items.Count == 0)
                {
                    Line("no " + setting.Keyword);
                }

                foreach (var item in items)
                {
                    Line(setting.Keyword + " add " + item);
                }

                return;
            }

            case ConfigShape.Map:
            {
                var map = value as JsonObject;
                if (map is null || map.Count == 0)
                {
                    Line("no " + setting.Keyword);
                }

                foreach (var pair in map ?? new JsonObject())
                {
                    Line(setting.Keyword + " " + pair.Key + " " + AsString(pair.Value));
                }

                return;
            }
        }

        var display = DisplayValue(setting, value);
        Line(display is "(not set)" or "(empty)" ? "no " + setting.Keyword : setting.Keyword + " " + display);
    }

    /// <summary>
    /// Computes masked differences from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <returns>Lines prefixed with <c>-</c>, <c>+</c>, or <c>*</c> (secret changed); empty when equal.</returns>
    public static IReadOnlyList<string> Diff(JsonObject from, JsonObject to)
    {
        var before = Flatten(from);
        var after = Flatten(to);
        var lines = new List<string>();
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(path, out var oldEntry);
            after.TryGetValue(path, out var newEntry);
            if (oldEntry is not null && newEntry is not null && oldEntry.Comparable == newEntry.Comparable)
            {
                continue;
            }

            var secret = (oldEntry ?? newEntry)!.IsSecret;
            if (secret)
            {
                var state = newEntry is null || newEntry.Display == "(not set)" ? "removed" : oldEntry is null || oldEntry.Display == "(not set)" ? "set" : "changed";
                lines.Add($"* {path} <{state}>");
                continue;
            }

            if (oldEntry is not null)
            {
                lines.Add($"- {path} {oldEntry.Display}".TrimEnd());
            }

            if (newEntry is not null)
            {
                lines.Add($"+ {path} {newEntry.Display}".TrimEnd());
            }
        }

        return lines;
    }

    /// <summary>
    /// Returns CLI paths whose values differ between two models (values are never returned).
    /// </summary>
    public static IReadOnlyList<ConfigEntry> ChangedEntries(JsonObject from, JsonObject to)
    {
        var before = Flatten(from);
        var after = Flatten(to);
        var changed = new List<ConfigEntry>();
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(path, out var oldEntry);
            after.TryGetValue(path, out var newEntry);
            if (oldEntry?.Comparable != newEntry?.Comparable)
            {
                changed.Add((newEntry ?? oldEntry)!);
            }
        }

        return changed;
    }

    /// <summary>
    /// Writes every managed value of <paramref name="model"/> into the file document <paramref name="fileRoot"/>,
    /// preserving unrelated sections, unknown properties, and unknown properties of matching collection entries.
    /// </summary>
    public static void MergeInto(JsonObject fileRoot, JsonObject model)
    {
        MergeSection(ConfigurationSchema.Root, fileRoot, model);
    }

    /// <summary>Merges one section.</summary>
    private static void MergeSection(ConfigSection section, JsonObject target, JsonObject source)
    {
        foreach (var setting in section.Settings)
        {
            SetValue(target, setting, GetValue(source, setting));
        }

        foreach (var collection in section.Collections)
        {
            var existing = target[collection.JsonName] as JsonArray ?? new JsonArray();
            var merged = new JsonArray();
            foreach (var item in (source[collection.JsonName] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var key = AsString(item[collection.KeyJsonName]) ?? string.Empty;
                var entry = FindItem(existing, collection, key)?.DeepClone() as JsonObject ?? new JsonObject();
                entry[collection.KeyJsonName] = JsonValue.Create(key);
                foreach (var setting in collection.Settings)
                {
                    SetValue(entry, setting, GetValue(item, setting));
                }

                merged.Add(entry);
            }

            target[collection.JsonName] = merged;
        }

        foreach (var child in section.Children)
        {
            MergeSection(
                child,
                ObjectAt(target, child.JsonPath, create: true)!,
                ObjectAt(source, child.JsonPath, create: false) ?? new JsonObject());
        }
    }

    /// <summary>
    /// Writes into the file document only the values the operator changed (<paramref name="candidate"/>
    /// differs from <paramref name="baseline"/>). Every other value keeps what the file already holds
    /// (or stays absent), so values supplied by higher-precedence providers — environment variables,
    /// <c>appsettings.{Environment}.json</c> — are never copied into the file.
    /// </summary>
    /// <param name="fileRoot">Parsed startup file (modified in place).</param>
    /// <param name="baseline">Effective configuration the candidate started from.</param>
    /// <param name="candidate">Operator's candidate.</param>
    public static void MergeChangesInto(JsonObject fileRoot, JsonObject baseline, JsonObject candidate)
    {
        MergeSectionChanges(ConfigurationSchema.Root, fileRoot, baseline, candidate);
    }

    /// <summary>Merges the changed values of one section; returns whether anything was written.</summary>
    private static bool MergeSectionChanges(ConfigSection section, JsonObject target, JsonObject baseline, JsonObject candidate)
    {
        var written = false;
        foreach (var setting in section.Settings)
        {
            written |= MergeSettingChange(target, setting, GetValue(baseline, setting), GetValue(candidate, setting));
        }

        foreach (var collection in section.Collections)
        {
            written |= MergeCollectionChanges(collection, target, baseline, candidate);
        }

        foreach (var child in section.Children)
        {
            var childTarget = ObjectAt(target, child.JsonPath, create: false);
            var scratch = childTarget ?? new JsonObject(NodeOptions);
            if (MergeSectionChanges(
                    child,
                    scratch,
                    ObjectAt(baseline, child.JsonPath, create: false) ?? new JsonObject(),
                    ObjectAt(candidate, child.JsonPath, create: false) ?? new JsonObject()))
            {
                written = true;
                if (childTarget is null)
                {
                    var parent = ObjectAt(target, child.JsonPath.Take(child.JsonPath.Count - 1), create: true)!;
                    parent[child.JsonPath[^1]] = scratch;
                }
            }
        }

        return written;
    }

    /// <summary>Merges the changed entries of one keyed collection; returns whether anything was written.</summary>
    private static bool MergeCollectionChanges(ConfigCollection collection, JsonObject target, JsonObject baseline, JsonObject candidate)
    {
        var baseItems = baseline[collection.JsonName] as JsonArray ?? new JsonArray();
        var candidateItems = candidate[collection.JsonName] as JsonArray ?? new JsonArray();
        if (JsonNode.DeepEquals(baseItems, candidateItems))
        {
            return false;
        }

        var existing = target[collection.JsonName] as JsonArray ?? new JsonArray();
        var merged = new JsonArray();
        foreach (var fileItem in existing.OfType<JsonObject>())
        {
            var key = AsString(fileItem[collection.KeyJsonName]) ?? string.Empty;
            var candidateItem = FindItem(candidateItems, collection, key);
            var baseItem = FindItem(baseItems, collection, key);
            if (candidateItem is null)
            {
                // Removed by the operator; entries the operator never saw are kept.
                if (baseItem is null)
                {
                    merged.Add(fileItem.DeepClone());
                }

                continue;
            }

            var entry = (JsonObject)fileItem.DeepClone();
            foreach (var setting in collection.Settings)
            {
                MergeSettingChange(
                    entry,
                    setting,
                    baseItem is null ? null : GetValue(baseItem, setting),
                    GetValue(candidateItem, setting),
                    force: baseItem is null);
            }

            merged.Add(entry);
        }

        foreach (var candidateItem in candidateItems.OfType<JsonObject>())
        {
            var key = AsString(candidateItem[collection.KeyJsonName]) ?? string.Empty;
            if (FindItem(existing, collection, key) is not null)
            {
                continue;
            }

            // Entries supplied only by another provider are written only when the operator changed them,
            // and then only the changed settings.
            var baseItem = FindItem(baseItems, collection, key);
            if (baseItem is not null && JsonNode.DeepEquals(baseItem, candidateItem))
            {
                continue;
            }

            var entry = new JsonObject(NodeOptions) { [collection.KeyJsonName] = JsonValue.Create(key) };
            foreach (var setting in collection.Settings)
            {
                MergeSettingChange(
                    entry,
                    setting,
                    baseItem is null ? null : GetValue(baseItem, setting),
                    GetValue(candidateItem, setting),
                    force: baseItem is null);
            }

            merged.Add(entry);
        }

        target[collection.JsonName] = merged;
        return true;
    }

    /// <summary>
    /// Writes <paramref name="after"/> when it differs from <paramref name="before"/> (or when
    /// <paramref name="force"/> is set). Maps are merged per key so untouched keys keep their file values.
    /// </summary>
    private static bool MergeSettingChange(JsonObject target, ConfigSetting setting, JsonNode? before, JsonNode? after, bool force = false)
    {
        if (!force && JsonNode.DeepEquals(before, after))
        {
            return false;
        }

        if (!force && setting.Shape == ConfigShape.Map && before is JsonObject beforeMap && after is JsonObject afterMap)
        {
            var map = GetValue(target, setting)?.DeepClone() as JsonObject ?? new JsonObject(NodeOptions);
            foreach (var (key, _) in beforeMap)
            {
                if (!afterMap.ContainsKey(key))
                {
                    map.Remove(key);
                }
            }

            foreach (var (key, value) in afterMap)
            {
                if (!beforeMap.TryGetPropertyValue(key, out var old) || !JsonNode.DeepEquals(old, value))
                {
                    map[key] = value?.DeepClone();
                }
            }

            SetValue(target, setting, map);
            return true;
        }

        SetValue(target, setting, after);
        return true;
    }

    /// <summary>Joins CLI path segments with a space.</summary>
    private static string Join(string prefix, string segment) =>
        prefix.Length == 0 ? segment : prefix + " " + segment;

    /// <summary>Strict IPv4 dotted-quad shape.</summary>
    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    /// <summary>RFC 1123 host name.</summary>
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNameRegex();

    /// <summary>Serilog source context name (for example <c>Microsoft.Hosting.Lifetime</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    internal static partial Regex LogSourceRegex();
}

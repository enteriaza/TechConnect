// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationInventoryTests.cs
//
// Purpose:
// Guards the configuration CLI settings inventory: every bound options
// property is either exposed by the schema or explicitly listed as unmanaged,
// secret settings are masked, and runtime-applicable settings are exactly the
// documented set.
//
// -----------------------------------------------------------------------------

using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using TechConnect.Server.Configuration;
using TechConnect.Server.Configuration.Management;

namespace TechConnect.Server.Tests.Configuration.Management;

/// <summary>
/// Settings inventory and classification tests.
/// </summary>
public sealed class ConfigurationInventoryTests
{
    /// <summary>Bound properties intentionally not exposed by the configuration CLI (preserved untouched on write).</summary>
    private static readonly string[] Unmanaged =
    [
        // Legacy server-wide key list superseded by per-user LocalUsers[*]:AuthorizedPublicKeys.
        "TechConnectServer:Ssh:AuthorizedPublicKeys"
    ];

    /// <summary>Settings applied to the running server after write (all others need a restart).</summary>
    private static readonly string[] RuntimeApplicable =
    [
        "TechConnectServer:Audit:Enabled",
        "TechConnectServer:Audit:MaximumEvents",
        "TechConnectServer:Dns:Servers",
        "TechConnectServer:Dns:QueryTimeoutSeconds",
        "Serilog:MinimumLevel:Default",
        "Serilog:MinimumLevel:Override"
    ];

    /// <summary>Settings whose values are secret.</summary>
    private static readonly string[] Secrets =
    [
        "TechConnectServer:Ssh:HostPrivateKey",
        "TechConnectServer:Authentication:Local:EnableSecretHash",
        "TechConnectServer:Authentication:Local:EnableSecrets[*]:SecretHash",
        "TechConnectServer:Authentication:Local:LocalUsers[*]:PasswordHash",
        "TechConnectServer:Authentication:Radius:PrimaryServer:SharedSecret",
        "TechConnectServer:Authentication:Radius:SecondaryServer:SharedSecret"
    ];

    /// <summary>Schema paths.</summary>
    private static Dictionary<string, ConfigSetting> SchemaPaths() =>
        ConfigurationSchema.EnumerateSettingPaths().ToDictionary(p => p.Path, p => p.Setting, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every bound options property is managed by the schema or explicitly unmanaged.</summary>
    [Fact]
    public void EveryBoundOption_IsManagedOrExplicitlyUnmanaged()
    {
        var bound = new List<string>();
        Collect(typeof(TechConnectServerOptions), "TechConnectServer", bound);
        Collect(typeof(FileLoggingOptions), FileLoggingOptions.SectionName, bound);
        var schema = SchemaPaths();

        bound.Except(Unmanaged, StringComparer.OrdinalIgnoreCase)
            .Where(p => !schema.ContainsKey(p))
            .Should().BeEmpty("every bound option needs a CLI setting or an explicit unmanaged entry");
        Unmanaged.Should().OnlyContain(p => !schema.ContainsKey(p));
    }

    /// <summary>Every schema setting maps to a bound property or a Serilog key (no phantom settings).</summary>
    [Fact]
    public void EverySchemaSetting_MapsToARealKey()
    {
        var bound = new List<string>();
        Collect(typeof(TechConnectServerOptions), "TechConnectServer", bound);
        Collect(typeof(FileLoggingOptions), FileLoggingOptions.SectionName, bound);

        SchemaPaths().Keys
            .Where(p => !p.StartsWith("Serilog:", StringComparison.OrdinalIgnoreCase))
            .Except(bound, StringComparer.OrdinalIgnoreCase)
            .Should().BeEmpty();
    }

    /// <summary>The runtime-applicable set is exactly the documented set.</summary>
    [Fact]
    public void RuntimeApplicableSettings_AreExactlyTheDocumentedSet()
    {
        SchemaPaths().Where(p => p.Value.ApplyMode == ConfigApplyMode.Runtime).Select(p => p.Key)
            .Should().BeEquivalentTo(RuntimeApplicable);
    }

    /// <summary>Secrets are classified as secret and render masked (shown as set/not set, never the value).</summary>
    [Fact]
    public void Secrets_AreClassifiedAndMasked()
    {
        var schema = SchemaPaths();
        schema.Where(p => p.Value.IsSecret).Select(p => p.Key).Should().BeEquivalentTo(Secrets);
        foreach (var path in Secrets)
        {
            var setting = schema[path];
            ConfigurationModel.DisplayValue(setting, JsonValue.Create("value-xyz")).Should().Be("<hidden>");
            ConfigurationModel.DisplayValue(setting, JsonValue.Create(string.Empty)).Should().Be("(not set)");
        }
    }

    /// <summary>Collects leaf configuration paths of an options type.</summary>
    private static void Collect(Type type, string prefix, List<string> paths)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var path = prefix + ":" + property.Name;
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType))
            {
                var element = propertyType.GetGenericArguments().FirstOrDefault() ?? typeof(string);
                if (IsLeaf(element))
                {
                    paths.Add(path);
                }
                else
                {
                    Collect(element, path + "[*]", paths);
                }
            }
            else if (IsLeaf(propertyType))
            {
                paths.Add(path);
            }
            else
            {
                Collect(propertyType, path, paths);
            }
        }
    }

    /// <summary>Scalar types bound as a single value.</summary>
    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
}

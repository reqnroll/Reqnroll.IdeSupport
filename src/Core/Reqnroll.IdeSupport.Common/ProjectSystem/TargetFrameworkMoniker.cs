#nullable disable
using Reqnroll;
using System;
using System.Linq;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>TargetFrameworkMoniker</summary>
public class TargetFrameworkMoniker
{
    /// <summary>The platform identifier used in TFMs for .NET Core / modern .NET (e.g. .NET 5+).</summary>
    public const string NetCorePlatform = ".NETCoreApp";
    /// <summary>The platform identifier used in TFMs for the .NET Framework.</summary>
    public const string NetFrameworkPlatform = ".NETFramework";

    private TargetFrameworkMoniker(string value)
    {
        Value = value;

        if (value != null)
        {
            var parts = value.Split(',');
            Platform = parts[0].Trim();
            const string versionPartPrefix = "Version=v";
            var versionPart = parts.FirstOrDefault(p => p.StartsWith(versionPartPrefix));
            if (versionPart != null)
            {
                var versionString = versionPart.Substring(versionPartPrefix.Length);
                if (Version.TryParse(versionString, out var version))
                    Version = version;
            }
        }
    }

    // e.g.
    // * .NET 8:    .NETCoreApp,Version=v8.0
    // * .NET 7:    .NETCoreApp,Version=v7.0
    // * .NET 6:    .NETCoreApp,Version=v6.0
    // * .NET 5:    .NETCoreApp,Version=v5.0
    // * .NET Core: .NETCoreApp,Version=v2.1
    // * .NET Fw:   .NETFramework,Version=v4.5.2
    /// <summary>Gets the raw target framework moniker string (e.g. <c>.NETCoreApp,Version=v8.0</c>).</summary>
    public string Value { get; }

    /// <summary>Gets the platform portion of the moniker (e.g. <c>.NETCoreApp</c> or <c>.NETFramework</c>).</summary>
    public string Platform { get; }
    /// <summary>Gets the parsed version portion of the moniker, or <c>null</c> if none was present.</summary>
    public Version Version { get; }

    /// <summary>Gets whether a specific framework version was parsed from the moniker.</summary>
    public bool HasVersion => Version != null;
    /// <summary>Gets whether this moniker targets .NET Core / modern .NET.</summary>
    public bool IsNetCore => NetCorePlatform.Equals(Platform);
    /// <summary>Gets whether this moniker targets the .NET Framework.</summary>
    public bool IsNetFramework => NetFrameworkPlatform.Equals(Platform);

    /// <summary>Creates a <see cref="TargetFrameworkMoniker"/> from its raw string form, or <c>null</c> if <paramref name="value"/> is <c>null</c>.</summary>
    public static TargetFrameworkMoniker Create(string value) =>
        value == null ? null : new TargetFrameworkMoniker(value);

    /// <summary>Returns the raw target framework moniker string.</summary>
    public override string ToString() => Value;

    #region Equality

    /// <summary>Determines whether this instance has the same raw moniker value as <paramref name="other"/>.</summary>
    protected bool Equals(TargetFrameworkMoniker other) => Value == other.Value;

    /// <summary>Determines whether <paramref name="obj"/> is a <see cref="TargetFrameworkMoniker"/> with the same raw moniker value.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((TargetFrameworkMoniker) obj);
    }

    /// <summary>Returns a hash code derived from the raw moniker value.</summary>
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Determines whether two monikers have the same raw value.</summary>
    public static bool operator ==(TargetFrameworkMoniker left, TargetFrameworkMoniker right) => Equals(left, right);

    /// <summary>Determines whether two monikers have different raw values.</summary>
    public static bool operator !=(TargetFrameworkMoniker left, TargetFrameworkMoniker right) => !Equals(left, right);

    #endregion
}

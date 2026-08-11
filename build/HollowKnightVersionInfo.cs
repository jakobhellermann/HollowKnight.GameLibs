using Fallout.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace _build;

[TypeConverter(typeof(Converter))]
public record HollowKnightVersionInfo
{
    public const uint STEAM_APPID = 367520;
    public const uint STEAM_DEPOT_ID_WINDOWS = 367521;
    public const uint STEAM_DEPOT_ID_MAC = 367522;
    public const uint STEAM_DEPOT_ID_LINUX = 367523;

    public static IEnumerable<HollowKnightVersionInfo> AllVersions => [
        .. typeof(HollowKnightVersionInfo).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.FieldType == typeof(HollowKnightVersionInfo))
            .Select(x => (HollowKnightVersionInfo)x.GetValue(null))
    ];
    public static IEnumerable<string> AllVersionStrings => AllVersions.Select(x => x.Version);

    public required string Version { get; init; }
    public required ulong WindowsManifestId { get; init; }
    public required ulong MacManifestId { get; init; }
    public required ulong LinuxManifestId { get; init; }

    public static readonly HollowKnightVersionInfo _1_2_2_1 = new()
    {
        Version = "1.2.2.1",
        WindowsManifestId = 648876203478229944,
        MacManifestId = 9202392574951903457,
        LinuxManifestId = 8836863544523989656
    };

    public static readonly HollowKnightVersionInfo _1_4_3_2 = new()
    {
        Version = "1.4.3.2",
        WindowsManifestId = 7100970131266256005,
        MacManifestId = 3397268605819178029,
        LinuxManifestId = 4631363637747835650
    };

    public static readonly HollowKnightVersionInfo _1_5_78_11833 = new()
    {
        Version = "1.5.78.11833",
        WindowsManifestId = 4539921744901848404,
        MacManifestId = 7770537498842372731,
        LinuxManifestId = 5829533265112705522
    };

    public static readonly HollowKnightVersionInfo _1_5_12620 = new()
    {
        Version = "1.5.12620",
        WindowsManifestId = 257781644874438846,
        MacManifestId = 6809616861563627670,
        LinuxManifestId = 708613018541602983
    };

    public sealed override string ToString() => Version;
    public string ToDetailedString()
    {
        StringBuilder sb = new();
        sb.Append("{ ");
        PrintMembers(sb);
        sb.Append(" }");
        return sb.ToString();
    }
    public static implicit operator string(HollowKnightVersionInfo v) => v.Version;

    public class Converter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is string str)
            {
                List<HollowKnightVersionInfo> matches = [.. AllVersions.Where(v => v.Version == str)];
                Assert.HasSingleItem(matches);
                return matches[0];
            }
            return base.ConvertFrom(context, culture, value);
        }
    }
}

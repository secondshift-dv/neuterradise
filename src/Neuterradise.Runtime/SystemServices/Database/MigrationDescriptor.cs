using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Neuterradise.App.SystemServices.Database;

public sealed record MigrationDescriptor
{
    private static readonly Regex ValidName = new(
        "^[a-z0-9]+(?:_[a-z0-9]+)*$",
        RegexOptions.CultureInvariant);

    private MigrationDescriptor(
        int version,
        string name,
        string sourceName,
        string sql,
        string checksum)
    {
        Version = version;
        Name = name;
        SourceName = sourceName;
        Sql = sql;
        Checksum = checksum;
    }

    public int Version { get; }

    public string Name { get; }

    public string SourceName { get; }

    public string Sql { get; }

    public string Checksum { get; }

    public static MigrationDescriptor FromSql(
        int version,
        string name,
        string sql,
        string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return FromUtf8Bytes(
            version,
            name,
            Encoding.UTF8.GetBytes(sql),
            sourceName ?? $"{version:D4}_{name}.sql");
    }

    public static MigrationDescriptor FromUtf8Bytes(
        int version,
        string name,
        ReadOnlySpan<byte> utf8Bytes,
        string sourceName)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Migration versions must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (!ValidName.IsMatch(name))
        {
            throw new ArgumentException(
                "Migration names must contain only lowercase letters, digits, and separating underscores.",
                nameof(name));
        }

        var bytes = utf8Bytes.ToArray();
        var sqlBytes = bytes.AsSpan();
        if (sqlBytes.Length >= 3
            && sqlBytes[0] == 0xEF
            && sqlBytes[1] == 0xBB
            && sqlBytes[2] == 0xBF)
        {
            sqlBytes = sqlBytes[3..];
        }

        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        var sql = strictUtf8.GetString(sqlBytes);
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        return new MigrationDescriptor(version, name, sourceName, sql, checksum);
    }
}

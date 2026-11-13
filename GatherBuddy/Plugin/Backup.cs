using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace GatherBuddy.Plugin;

public static partial class Backup
{
    public const int MaxNumBackups = 10;

    public static void CreateAutomaticBackup(Logger logger, DirectoryInfo dir, IReadOnlyCollection<FileInfo> files)
    {
        try
        {
            var configDirectory = dir.Parent!.FullName;
            var directory = CreateBackupDirectory(dir);
            var existingFiles = new List<FileInfo>(files.Count);
            foreach (var file in files)
            {
                if (File.Exists(file.FullName))
                {
                    existingFiles.Add(file);
                }
            }

            var (newest, oldest, count) = CheckExistingBackups(directory);
            if (newest != null && !DiffersFromBackup(logger, newest, configDirectory, existingFiles.Count))
            {
                return;
            }

            CreateBackupFile(existingFiles, Path.Combine(directory.FullName, $"{DateTime.Now:yyyyMMddHHmmss}.zip"), configDirectory);
            if (count > MaxNumBackups)
            {
                oldest!.Delete();
            }
        }
        catch (Exception e)
        {
            logger.Error($"Could not create backups:\n{e}");
        }
    }

    private static DirectoryInfo CreateBackupDirectory(DirectoryInfo dir)
        => Directory.CreateDirectory(Path.Combine(dir.Parent!.Parent!.FullName, "backups", dir.Name));

    private static (FileInfo? Newest, FileInfo? Oldest, int Count) CheckExistingBackups(DirectoryInfo backupDirectory)
    {
        var count = 0;
        FileInfo? newest = null;
        FileInfo? oldest = null;
        foreach (var file in backupDirectory.EnumerateFiles("*.zip"))
        {
            if (!FormatRegex().IsMatch(file.Name))
            {
                continue;
            }

            ++count;
            var time = file.CreationTimeUtc;
            if ((oldest?.CreationTimeUtc ?? DateTime.MaxValue) > time)
            {
                oldest = file;
            }

            if ((newest?.CreationTimeUtc ?? DateTime.MinValue) < time)
            {
                newest = file;
            }
        }

        return (newest, oldest, count);
    }

    [GeneratedRegex(@"^\d{14}\.zip$", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex FormatRegex();

    private static bool DiffersFromBackup(Logger logger, FileInfo backup, string configDirectory, int fileCount)
    {
        try
        {
            using var zip = ZipFile.OpenRead(backup.FullName);
            if (fileCount != zip.Entries.Count)
            {
                return true;
            }

            foreach (var entry in zip.Entries)
            {
                var file = Path.Combine(configDirectory, entry.FullName);
                if (!File.Exists(file))
                {
                    return true;
                }

                using var current = File.OpenRead(file);
                using var old = entry.Open();
                if (!StreamsEqual(current, old))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e)
        {
            logger.Warning($"Could not read the newest backup file {backup.FullName}:\n{e}");
            return true;
        }
    }

    private static void CreateBackupFile(List<FileInfo> files, string fileName, string configDirectory)
    {
        using var zip = ZipFile.Open(fileName, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            zip.CreateEntryFromFile(file.FullName, Path.GetRelativePath(configDirectory, file.FullName), CompressionLevel.Optimal);
        }
    }

    private static bool StreamsEqual(Stream lhs, Stream rhs)
    {
        const int bufferSize = 4096;
        Span<byte> left = stackalloc byte[bufferSize];
        Span<byte> right = stackalloc byte[bufferSize];
        while (true)
        {
            var readLeft = lhs.ReadAtLeast(left, bufferSize, false);
            var readRight = rhs.ReadAtLeast(right, bufferSize, false);
            if (readLeft != readRight || !left[..readLeft].SequenceEqual(right[..readRight]))
            {
                return false;
            }

            if (readLeft < bufferSize)
            {
                return true;
            }
        }
    }
}

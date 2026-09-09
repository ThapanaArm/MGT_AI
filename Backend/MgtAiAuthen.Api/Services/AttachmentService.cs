using System.Security.Cryptography;
using System.Text;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>ไฟล์ที่ผ่านการตรวจและเก็บลง disk แล้ว รอผูกกับข้อความ</summary>
public record StoredFile(
    string FileName,
    string ContentType,
    string FileKind,
    long SizeBytes,
    string Sha256,
    string RelativePath,
    string? ExtractedText);

public interface IAttachmentService
{
    /// <summary>
    /// ตรวจและเก็บไฟล์ทั้งชุดลง disk — โยน <see cref="AppException"/> ถ้าไฟล์ใดไม่ผ่านเงื่อนไข
    /// ไฟล์ที่เก็บไปแล้วก่อนเจอ error จะถูกลบทิ้งให้ ไม่ทิ้งขยะไว้บน disk
    /// </summary>
    Task<List<StoredFile>> StoreAsync(IReadOnlyList<IFormFile> files, CancellationToken ct = default);

    /// <summary>Reads a file back as bytes, either to send to Claude or for an auditor to download.</summary>
    Task<byte[]> ReadAsync(string relativePath, CancellationToken ct = default);

    /// <summary>เส้นทางเต็มบน disk — ใช้ตรวจว่าไฟล์ยังอยู่หรือไม่</summary>
    string ResolvePath(string relativePath);

    /// <summary>ลบไฟล์ที่เก็บไปแล้ว (ใช้ตอน rollback)</summary>
    void TryDelete(string relativePath);
}

public class AttachmentService(
    IOptions<UploadOptions> options,
    IHostEnvironment environment,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private static readonly string[] TextExtensions = [".txt", ".csv", ".md", ".json", ".log"];

    private readonly UploadOptions _options = options.Value;

    public async Task<List<StoredFile>> StoreAsync(
        IReadOnlyList<IFormFile> files, CancellationToken ct = default)
    {
        if (files.Count > _options.MaxFilesPerMessage)
        {
            throw new AppException(
                $"You may attach at most {_options.MaxFilesPerMessage} files per message (received {files.Count})");
        }

        long totalBytes = files.Sum(f => f.Length);
        if (totalBytes > _options.MaxTotalBytesPerMessage)
        {
            throw new AppException(
                $"Total attachment size must not exceed {_options.MaxTotalMbPerMessage} MB " +
                $"(received {totalBytes / 1024.0 / 1024.0:F1} MB)");
        }

        List<StoredFile> stored = [];

        try
        {
            foreach (IFormFile file in files)
            {
                stored.Add(await StoreOneAsync(file, ct));
            }
        }
        catch
        {
            // ไม่ทิ้งไฟล์ค้างบน disk เมื่อชุดนี้ล้มกลางทาง
            foreach (StoredFile done in stored)
            {
                TryDelete(done.RelativePath);
            }

            throw;
        }

        return stored;
    }

    public async Task<byte[]> ReadAsync(string relativePath, CancellationToken ct = default)
    {
        string fullPath = ResolvePath(relativePath);

        if (!File.Exists(fullPath))
        {
            throw AppException.NotFound(
                "Attachment file not found on the server — it may have been deleted (metadata remains in the log)");
        }

        return await File.ReadAllBytesAsync(fullPath, ct);
    }

    public string ResolvePath(string relativePath)
    {
        string root = GetRootPath();

        // กัน path traversal: เส้นทางที่ประกอบแล้วต้องอยู่ใต้ root เท่านั้น
        string combined = Path.GetFullPath(Path.Combine(root, relativePath));
        string normalizedRoot = Path.GetFullPath(root);

        if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("Invalid file path");
        }

        return combined;
    }

    public void TryDelete(string relativePath)
    {
        try
        {
            string fullPath = ResolvePath(relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete file {Path}", relativePath);
        }
    }

    private async Task<StoredFile> StoreOneAsync(IFormFile file, CancellationToken ct)
    {
        string fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new AppException("Attachment has no file name");
        }

        if (fileName.Length > 300)
        {
            fileName = fileName[^300..];
        }

        if (file.Length == 0)
        {
            throw new AppException($"File \"{fileName}\" is empty");
        }

        if (file.Length > _options.MaxFileBytes)
        {
            throw new AppException(
                $"File \"{fileName}\" exceeds {_options.MaxFileMb} MB " +
                $"(size {file.Length / 1024.0 / 1024.0:F1} MB)");
        }

        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!_options.EffectiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(
                $"Unsupported file extension \"{extension}\" — allowed types: {string.Join(", ", _options.EffectiveExtensions)}");
        }

        byte[] content;
        await using (Stream input = file.OpenReadStream())
        {
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        string kind = ResolveKind(extension);

        // ตรวจ magic bytes ของ PDF/รูปภาพ กันการเปลี่ยนนามสกุลเพื่อเลี่ยงข้อจำกัด
        ValidateSignature(fileName, extension, kind, content);

        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // แยกโฟลเดอร์ตามวันเพื่อไม่ให้ไฟล์กองในโฟลเดอร์เดียวจนช้า
        string relativeDir = Path.Combine(
            DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"));
        string storedName = $"{Guid.NewGuid():N}{extension}";
        string relativePath = Path.Combine(relativeDir, storedName);

        string fullPath = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content, ct);

        string? extractedText = kind == FileKinds.Text ? DecodeText(content) : null;

        return new StoredFile(
            FileName: fileName,
            ContentType: ResolveContentType(extension, file.ContentType),
            FileKind: kind,
            SizeBytes: content.Length,
            Sha256: sha256,
            RelativePath: relativePath.Replace('\\', '/'),
            ExtractedText: extractedText);
    }

    private static string ResolveKind(string extension)
    {
        if (ImageExtensions.Contains(extension)) return FileKinds.Image;
        if (extension == ".pdf") return FileKinds.Pdf;
        if (TextExtensions.Contains(extension)) return FileKinds.Text;

        throw new AppException($"Unsupported file extension \"{extension}\"");
    }

    /// <summary>
    /// ตรวจ magic bytes — ถ้าเปลี่ยนนามสกุล .exe เป็น .pdf จะถูกจับได้ที่นี่
    /// ไฟล์ข้อความไม่มี signature ที่แน่นอน จึงข้ามการตรวจส่วนนี้
    /// </summary>
    private static void ValidateSignature(string fileName, string extension, string kind, byte[] content)
    {
        bool ok = kind switch
        {
            FileKinds.Pdf => content.Length > 4 && content[0] == 0x25 && content[1] == 0x50
                             && content[2] == 0x44 && content[3] == 0x46,   // %PDF
            FileKinds.Image => extension switch
            {
                ".png" => content.Length > 8 && content[0] == 0x89 && content[1] == 0x50
                          && content[2] == 0x4E && content[3] == 0x47,
                ".jpg" or ".jpeg" => content.Length > 3 && content[0] == 0xFF && content[1] == 0xD8,
                ".gif" => content.Length > 3 && content[0] == 0x47 && content[1] == 0x49 && content[2] == 0x46,
                ".webp" => content.Length > 12 && content[0] == 0x52 && content[1] == 0x49
                           && content[8] == 0x57 && content[9] == 0x45,     // RIFF....WEBP
                _ => true,
            },
            _ => true,
        };

        if (!ok)
        {
            throw new AppException(
                $"File \"{fileName}\" content does not match its {extension} extension — rejected for security reasons");
        }
    }

    /// <summary>Decodes a text file as UTF-8 (honouring a BOM) and truncates it past the configured limit.</summary>
    private string? DecodeText(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(content)
                .TrimStart('﻿');
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to decode text file as UTF-8");
            return null;
        }

        if (text.Length <= _options.MaxTextChars)
        {
            return text;
        }

        logger.LogInformation(
            "Text file is {Length} characters, over the {Max} limit — the excess was truncated",
            text.Length, _options.MaxTextChars);

        return text[.._options.MaxTextChars]
               + $"\n\n[...truncated content beyond {_options.MaxTextChars:N0} characters...]";
    }

    private static string ResolveContentType(string extension, string? provided) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".md" => "text/markdown",
        _ => string.IsNullOrWhiteSpace(provided) ? "text/plain" : provided,
    };

    private string GetRootPath()
        => Path.IsPathRooted(_options.RootPath)
            ? _options.RootPath
            : Path.Combine(environment.ContentRootPath, _options.RootPath);
}

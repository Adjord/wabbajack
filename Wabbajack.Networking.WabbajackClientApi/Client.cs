﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.DTOs.CDN;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.DTOs.Logins;
using Wabbajack.DTOs.ModListValidation;
using Wabbajack.DTOs.Validation;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Networking.Http;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.VFS;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Wabbajack.Networking.WabbajackClientApi;

public class Client
{
    public static readonly long UploadedFileBlockSize = (long) 1024 * 1024 * 2;

    private readonly HttpClient _client;
    private readonly Configuration _configuration;
    private readonly DTOSerializer _dtos;
    private readonly IResource<FileHashCache> _hashLimiter;
    private readonly IResource<HttpClient> _limiter;
    private readonly ILogger<Client> _logger;
    private readonly ParallelOptions _parallelOptions;

    private readonly ITokenProvider<WabbajackApiState> _token;


    public Client(ILogger<Client> logger, HttpClient client, ITokenProvider<WabbajackApiState> token,
        DTOSerializer dtos,
        IResource<HttpClient> limiter, IResource<FileHashCache> hashLimiter, Configuration configuration)
    {
        _configuration = configuration;
        _token = token;
        _client = client;
        _logger = logger;
        _dtos = dtos;
        _limiter = limiter;
        _hashLimiter = hashLimiter;
    }

    private async ValueTask<HttpRequestMessage> MakeMessage(HttpMethod method, Uri uri, HttpContent? content = null)
    {
        var msg = new HttpRequestMessage(method, uri);
        var key = (await _token.Get())!;
        msg.Headers.Add(_configuration.MetricsKeyHeader, key.MetricsKey);
        if (!string.IsNullOrWhiteSpace(key.AuthorKey))
            msg.Headers.Add(_configuration.AuthorKeyHeader, key.AuthorKey);

        if (content != null)
            msg.Content = content;
        return msg;
    }

    public async Task SendMetric(string action, string subject)
    {
        var msg = await MakeMessage(HttpMethod.Get,
            new Uri($"{_configuration.BuildServerUrl}metrics/{action}/{subject}"));
        await _client.SendAsync(msg);
    }

    public async Task<ServerAllowList> LoadDownloadAllowList()
    {
        var str = await _client.GetStringAsync(_configuration.ServerAllowList);
        var d = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .Build();
        return d.Deserialize<ServerAllowList>(str);
    }

    public async Task<ServerAllowList> LoadMirrorAllowList()
    {
        var str = await _client.GetStringAsync(_configuration.MirrorAllowList);
        var d = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .Build();
        return d.Deserialize<ServerAllowList>(str);
    }

    public async Task<Dictionary<Hash, ValidatedArchive>> LoadUpgradedArchives()
    {
        return (await _client.GetFromJsonAsync<ValidatedArchive[]>(_configuration.UpgradedArchives, _dtos.Options))!
            .ToDictionary(d => d.Original.Hash);
    }

    public async Task<Archive[]> GetGameArchives(Game game, string version)
    {
        var url = $"https://raw.githubusercontent.com/wabbajack-tools/indexed-game-files/master/{game}/{version}.json";
        return await _client.GetFromJsonAsync<Archive[]>(url, _dtos.Options) ?? Array.Empty<Archive>();
    }

    public async Task<Archive[]> GetArchivesForHash(Hash hash)
    {
        var msg = await MakeMessage(HttpMethod.Get,
            new Uri($"{_configuration.BuildServerUrl}mod_files/by_hash/{hash.ToHex()}"));
        return await _client.GetFromJsonAsync<Archive[]>(_limiter, msg, _dtos.Options) ?? Array.Empty<Archive>();
    }

    public Uri GetMirrorUrl(Hash archiveHash)
    {
        return new Uri($"{_configuration.MirrorServerUrl}{archiveHash.ToHex()}");
    }

    public async Task SendModListDefinition(ModList modList)
    {
        await using var fs = new MemoryStream();
        await using var gzip = new GZipStream(fs, CompressionLevel.SmallestSize, true);
        await _dtos.Serialize(modList, gzip);
        await gzip.DisposeAsync();
        fs.Position = 0;

        var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{_configuration.BuildServerUrl}list_definitions/ingest");
        msg.Headers.Add("x-compressed-body", "gzip");
        msg.Content = new StreamContent(fs);
        await _client.SendAsync(msg);
    }

    public async Task<ModListSummary[]> GetListStatuses()
    {
        return await _client.GetFromJsonAsync<ModListSummary[]>(
            "https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/reports/modListSummary.json",
            _dtos.Options) ?? Array.Empty<ModListSummary>();
    }

    public async Task<ValidatedModList> GetDetailedStatus(string machineURL)
    {
        return (await _client.GetFromJsonAsync<ValidatedModList>(
            $"https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/reports/{machineURL}/status.json",
            _dtos.Options))!;
    }
    
    IEnumerable<PartDefinition> Blocks(long size)
    {
        for (long block = 0; block * UploadedFileBlockSize < size; block++)
            yield return new PartDefinition
            {
                Index = block,
                Size = Math.Min(UploadedFileBlockSize, size - block * UploadedFileBlockSize),
                Offset = block * UploadedFileBlockSize
            };
    }


    public async Task<FileDefinition> GenerateFileDefinition(AbsolutePath path)
    {

        var parts = Blocks(path.Size()).ToArray();
        var definition = new FileDefinition
        {
            OriginalFileName = path.FileName,
            Size = path.Size(),
            Hash = await path.Hash(),
            Parts = await parts.PMapAll(async part =>
            {
                var buffer = new byte[part.Size];
                using var job = await _hashLimiter.Begin("Hashing part", part.Size, CancellationToken.None);
                await using (var fs = path.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Position = part.Offset;
                    await fs.ReadAsync(buffer);
                }

                part.Hash = await buffer.Hash(job);
                return part;
            }).ToArray()
        };

        return definition;
    }

    public async Task<ModlistMetadata[]> LoadLists(bool includeUnlisted = false)
    {
        var lists = new[]
            {
                "https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/modlists.json",
                "https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/utility_modlists.json",
                "https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/unlisted_modlists.json"
            }
            .Take(includeUnlisted ? 3 : 2);

        return await lists.PMapAll(async url =>
                await _client.GetFromJsonAsync<ModlistMetadata[]>(_limiter, new HttpRequestMessage(HttpMethod.Get, url),
                    _dtos.Options)!)
            .SelectMany(x => x)
            .ToArray();
    }

    public Uri GetPatchUrl(Hash upgradeHash, Hash archiveHash)
    {
        return new Uri($"{_configuration.PatchBaseAddress}{upgradeHash.ToHex()}_{archiveHash.ToHex()}");
    }

    public async Task<ValidatedArchive> UploadPatch(ValidatedArchive validated, Stream data)
    {
        _logger.LogInformation("Uploading Patch {From} {To}", validated.Original.Hash, validated.PatchedFrom!.Hash);
        var name = $"{validated.Original.Hash.ToHex()}_{validated.PatchedFrom.Hash.ToHex()}";
        
        var blocks = Blocks(data.Length).ToArray();
        foreach (var block in blocks)
        {
            _logger.LogInformation("Uploading Block {Idx}/{Max}", block.Index, blocks.Length);
            data.Position = block.Offset;
            var blockData = new byte[block.Size];
            await data.ReadAsync(blockData);
            var hash = await blockData.Hash();

            using var result = await _client.SendAsync(await MakeMessage(HttpMethod.Post,
                new Uri($"{_configuration.BuildServerUrl}patches?name={name}&start={block.Offset}"),
                new ByteArrayContent(blockData)));
            if (!result.IsSuccessStatusCode)
                throw new HttpException(result);

            var resultHash = Hash.FromHex(await result.Content.ReadAsStringAsync());
            if (resultHash != hash)
                throw new Exception($"Result Hash does not match expected hash {hash} vs {resultHash}");
        }

        validated.PatchUrl = new Uri($"https://patches.wabbajack.org/{name}");

        return validated;
    }

    public async Task AddForceHealedPatch(ValidatedArchive validated)
    {
        var oldData = await GetGithubFile<ValidatedArchive[]>("wabbajack-tools", "mod-lists", "configs/forced_healing.json");
        var content = oldData.Content.Append(validated).ToArray();
        await UpdateGitHubFile("wabbajack-tools", "mod-lists", "configs/forced_healing.json", content, oldData.Sha);
    }

    private async Task UpdateGitHubFile<T>(string owner, string repo, string path, T content, string oldSha)
    {
        var json = _dtos.Serialize(content, writeIndented: true);
        var msg = await MakeMessage(HttpMethod.Post,
            new Uri($"{_configuration.BuildServerUrl}github/?owner={owner}&repo={repo}&path={path}&oldSha={oldSha}"));

        msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var result = await _client.SendAsync(msg);
        if (!result.IsSuccessStatusCode)
            throw new HttpException(result);
    }

    private async Task<(string Sha, T Content)> GetGithubFile<T>(string owner, string repo, string path, CancellationToken? token = null)
    {
        token ??= CancellationToken.None;
        
        var msg = await MakeMessage(HttpMethod.Get,
            new Uri($"{_configuration.BuildServerUrl}github/?owner={owner}&repo={repo}&path={path}"));
        using var oldData = await _client.SendAsync(msg, token.Value);
        if (!oldData.IsSuccessStatusCode)
            throw new HttpException(oldData);

        var sha = oldData.Headers.GetValues(_configuration.ResponseShaHeader).First();
        return (sha, (await oldData.Content.ReadFromJsonAsync<T>(_dtos.Options, token.Value))!);
    }


    public async Task UploadMirror(FileDefinition definition, AbsolutePath file)
    {
        var hashAsHex = definition.Hash.ToHex();
        _logger.LogInformation("Starting upload of {Name} ({Hash})", file.FileName, hashAsHex);
        
        using var result = await _client.SendAsync(await MakeMessage(HttpMethod.Put,
            new Uri($"{_configuration.BuildServerUrl}mirrored_files/create/{hashAsHex}"),
            new StringContent(_dtos.Serialize(definition), Encoding.UTF8, "application/json")));
        if (!result.IsSuccessStatusCode)
            throw new HttpException(result);
        
        _logger.LogInformation("Uploading Parts");

        await using var dataIn = file.Open(FileMode.Open);

        foreach (var (part, idx) in definition.Parts.Select((part, idx) => (part, idx)))
        {
            _logger.LogInformation("Uploading Part {Part}/{Max}", idx, definition.Parts.Length);

            dataIn.Position = part.Offset;
            var data = new byte[part.Size];
            await dataIn.ReadAsync(data);

            using var partResult = await _client.SendAsync(await MakeMessage(HttpMethod.Put,
                new Uri($"{_configuration.BuildServerUrl}mirrored_files/{hashAsHex}/part/{idx}"),
                new ByteArrayContent(data)));
            
            if (!partResult.IsSuccessStatusCode)
                throw new HttpException(result);
        }

        using var finalResult = await _client.SendAsync(await MakeMessage(HttpMethod.Put,
            new Uri($"{_configuration.BuildServerUrl}mirrored_files/{hashAsHex}/finish")));
        
        if (!finalResult.IsSuccessStatusCode)
            throw new HttpException(result);
    }

    public async Task<FileDefinition[]> GetAllMirroredFileDefinitions(CancellationToken token)
    {
        return (await _client.GetFromJsonAsync<FileDefinition[]>($"{_configuration.BuildServerUrl}mirrored_files",
            _dtos.Options, token))!;
    }

    public async Task<ValidatedArchive[]> GetAllPatches(CancellationToken token)
    {
        return (await _client.GetFromJsonAsync<ValidatedArchive[]>("https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/configs/forced_healing.json", _dtos.Options, token))!;
    }
}
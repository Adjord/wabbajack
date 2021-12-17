using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP.Helpers;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.Compiler.PatchCache;
using Wabbajack.Downloaders;
using Wabbajack.DTOs;
using Wabbajack.DTOs.ModListValidation;
using Wabbajack.DTOs.ServerResponses;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Installer;
using Wabbajack.Networking.Http;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.VFS;

namespace Wabbajack.CLI.Verbs;

public class ForceHeal : IVerb
{
    private readonly ILogger<ForceHeal> _logger;
    private readonly Client _client;
    private readonly DownloadDispatcher _downloadDispatcher;
    private readonly FileHashCache _fileHashCache;
    private readonly HttpClient _httpClient;

    public ForceHeal(ILogger<ForceHeal> logger, Client client, DownloadDispatcher downloadDispatcher, FileHashCache hashCache,
        HttpClient httpClient)
    {
        _logger = logger;
        _client = client;
        _downloadDispatcher = downloadDispatcher;
        _fileHashCache = hashCache;
        _httpClient = httpClient;
    }

    public Command MakeCommand()
    {
        var command = new Command("force-heal");
        command.Add(new Option<AbsolutePath>(new[] {"-f", "-from"}, "Old File"));
        command.Add(new Option<string>(new[] {"-t", "-to"}, "New File"));
        command.Description = "Creates a patch from New file to Old File and uploads it";
        command.Handler = CommandHandler.Create(Run);
        return command;
    }

    public async Task<int> Run(AbsolutePath from, AbsolutePath to)
    {
        var fromResolved = await Resolve(from);
        var toResolved = await Resolve(to);

        _logger.LogInformation("Creating patch");
        var outData = new MemoryStream();
        OctoDiff.Create( await @from.ReadAllBytesAsync(), await to.ReadAllBytesAsync(), outData);
        
        _logger.LogInformation("Created {Size} patch", outData.Length.FileSizeToString());

        outData.Position = 0;
        
        var validated = new ValidatedArchive
        {
            Original = fromResolved,
            PatchedFrom = toResolved,
            Status = ArchiveStatus.Updated
        };
        
        validated = await _client.UploadPatch(validated, outData);
        _logger.LogInformation("Patch Updated, validating result by downloading patch");

        using var patchStream = await _httpClient.GetAsync(validated.PatchUrl);
        if (!patchStream.IsSuccessStatusCode)
            throw new HttpException(patchStream);
        
        outData.Position = 0;
        var originalHash = outData.HashingCopy(Stream.Null, CancellationToken.None);
        var hash = await (await patchStream.Content.ReadAsStreamAsync()).HashingCopy(Stream.Null, CancellationToken.None);
        if (hash != await originalHash)
        {
            throw new Exception($"Patch on server does not match patch hash {await originalHash} vs {hash}");
        }

        _logger.LogInformation("Adding patch to forced_healing.json");
        await _client.AddForceHealedPatch(validated);
        _logger.LogInformation("Done, validation should trigger soon");
        return 0;
    }

    private async Task<Archive> Resolve(AbsolutePath file)
    {
        var meta = file.WithExtension(Ext.Meta);
        if (!meta.FileExists())
            throw new Exception($"Meta not found {meta}");
        
        var ini = meta.LoadIniFile();
        var state = await _downloadDispatcher.ResolveArchive(ini["General"].ToDictionary(d => d.KeyName, d => d.Value));

        _logger.LogInformation("Hashing {File}", file.FileName);
        var hash = await _fileHashCache.FileHashCachedAsync(file, CancellationToken.None);
        
        return new Archive
        {
            Hash = hash,
            Name = file.FileName.ToString(),
            Size = file.Size(),
            State = state!
        };
    }
}
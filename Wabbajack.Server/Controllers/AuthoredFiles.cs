﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Nettle;
using Wabbajack.Common;
using Wabbajack.DTOs.CDN;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Server.DataModels;
using Wabbajack.Server.DTOs;
using Wabbajack.Server.Extensions;
using Wabbajack.Server.Services;

namespace Wabbajack.BuildServer.Controllers;

[Authorize(Roles = "Author")]
[Route("/authored_files")]
public class AuthoredFiles : ControllerBase
{
    private static readonly Func<object, string> HandleGetListTemplate = NettleEngine.GetCompiler().RegisterWJFunctions().Compile(@"
            <html><body>
                <table>
                {{each $.files }}
                <tr>
                       <td><a href='https://authored-files.wabbajack.org/{{$.Definition.MungedName}}'>{{$.Definition.OriginalFileName}}</a></td>
                       <td>{{$.HumanSize}}</td>
                       <td>{{$.Definition.Author}}</td>
                       <td>{{$.Updated}}</td>
                       <td><a href='/authored_files/direct_link/{{$.Definition.MungedName}}'>(Slow) HTTP Direct Link</a></td>
                </tr>
                {{/each}}
                </table>
            </body></html>
        ");


    private readonly DTOSerializer _dtos;

    private readonly DiscordWebHook _discord;
    private readonly ILogger<AuthoredFiles> _logger;
    private readonly AppSettings _settings;
    private readonly AuthorFiles _authoredFiles;


    public AuthoredFiles(ILogger<AuthoredFiles> logger, AuthorFiles authorFiles, AppSettings settings, DiscordWebHook discord,
        DTOSerializer dtos)
    {
        _logger = logger;
        _settings = settings;
        _discord = discord;
        _dtos = dtos;
        _authoredFiles = authorFiles;
    }
    
    [HttpPut]
    [Route("{serverAssignedUniqueId}/part/{index}")]
    public async Task<IActionResult> UploadFilePart(CancellationToken token, string serverAssignedUniqueId, long index)
    {
        var user = User.FindFirstValue(ClaimTypes.Name);
        var definition = await _authoredFiles.ReadDefinitionForServerId(serverAssignedUniqueId);
        if (definition.Author != user)
            return Forbid("File Id does not match authorized user");
        _logger.Log(LogLevel.Information,
            $"Uploading File part {definition.OriginalFileName} - ({index} / {definition.Parts.Length})");
        
        var part = definition.Parts[index];

        await using var ms = new MemoryStream();
        await Request.Body.CopyToLimitAsync(ms, (int) part.Size, token);
        ms.Position = 0;
        if (ms.Length != part.Size)
            return BadRequest($"Couldn't read enough data for part {part.Size} vs {ms.Length}");

        var hash = await ms.Hash(token);
        if (hash != part.Hash)
            return BadRequest(
                $"Hashes don't match for index {index}. Sizes ({ms.Length} vs {part.Size}). Hashes ({hash} vs {part.Hash}");

        ms.Position = 0;
        await using var partStream = await _authoredFiles.CreatePart(definition.MungedName, (int)index);
        await ms.CopyToAsync(partStream, token);
        return Ok(part.Hash.ToBase64());
    }

    [HttpPut]
    [Route("create")]
    public async Task<IActionResult> CreateUpload()
    {
        var user = User.FindFirstValue(ClaimTypes.Name);

        var definition = (await _dtos.DeserializeAsync<FileDefinition>(Request.Body))!;

        _logger.Log(LogLevel.Information, "Creating File upload {originalFileName}", definition.OriginalFileName);

        definition.ServerAssignedUniqueId = Guid.NewGuid().ToString();
        definition.Author = user;
        await _authoredFiles.WriteDefinition(definition);
        
        await _discord.Send(Channel.Ham,
            new DiscordMessage
            {
                Content =
                    $"{user} has started uploading {definition.OriginalFileName} ({definition.Size.ToFileSizeString()})"
            });

        return Ok(definition.ServerAssignedUniqueId);
    }

    [HttpPut]
    [Route("{serverAssignedUniqueId}/finish")]
    public async Task<IActionResult> CreateUpload(string serverAssignedUniqueId)
    {
        var user = User.FindFirstValue(ClaimTypes.Name);
        var definition = await _authoredFiles.ReadDefinitionForServerId(serverAssignedUniqueId);
        if (definition.Author != user)
            return Forbid("File Id does not match authorized user");
        _logger.Log(LogLevel.Information, $"Finalizing file upload {definition.OriginalFileName}");

        await _discord.Send(Channel.Ham,
            new DiscordMessage
            {
                Content =
                    $"{user} has finished uploading {definition.OriginalFileName} ({definition.Size.ToFileSizeString()})"
            });

        var host = _settings.TestMode ? "test-files" : "authored-files";
        return Ok($"https://{host}.wabbajack.org/{definition.MungedName}");
    }

    [HttpDelete]
    [Route("{serverAssignedUniqueId}")]
    public async Task<IActionResult> DeleteUpload(string serverAssignedUniqueId)
    {
        var user = User.FindFirstValue(ClaimTypes.Name);
        var definition = (await _authoredFiles.AllAuthoredFiles())
            .First(f => f.Definition.ServerAssignedUniqueId == serverAssignedUniqueId)
            .Definition;
        if (definition.Author != user)
            return Forbid("File Id does not match authorized user");
        await _discord.Send(Channel.Ham,
            new DiscordMessage
            {
                Content =
                    $"{user} is deleting {definition.MungedName}, {definition.Size.ToFileSizeString()} to be freed"
            });
        _logger.Log(LogLevel.Information, $"Deleting upload {definition.OriginalFileName}");

        await _authoredFiles.DeleteFile(definition);
        return Ok();
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("")]
    public async Task<ContentResult> UploadedFilesGet()
    {
        var files = await _authoredFiles.AllAuthoredFiles();
        var response = HandleGetListTemplate(new {files = files.OrderByDescending(f => f.Updated).ToArray()});
        return new ContentResult
        {
            ContentType = "text/html",
            StatusCode = (int) HttpStatusCode.OK,
            Content = response
        };
    }
    
    [HttpGet]
    [AllowAnonymous]
    [Route("direct_link/{mungedName}")]
    public async Task DirectLink(string mungedName)
    {
        mungedName = _authoredFiles.DecodeName(mungedName);
        var definition = await _authoredFiles.ReadDefinition(mungedName);
        Response.Headers.ContentDisposition =
            new StringValues($"attachment; filename={definition.OriginalFileName}");
        Response.Headers.ContentType = new StringValues("application/octet-stream");
        foreach (var part in definition.Parts)
        {
            await using var partStream = await _authoredFiles.StreamForPart(mungedName, (int)part.Index);
            await partStream.CopyToAsync(Response.Body);
        }
    }
}
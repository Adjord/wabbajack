﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Alphaleonis.Win32.Filesystem;
using Compression.BSA;
using ICSharpCode.SharpZipLib.GZip;
using Newtonsoft.Json;
using OMODFramework;
using Wabbajack.Common.StatusFeed;
using Wabbajack.Common.StatusFeed.Errors;

namespace Wabbajack.Common
{
    public class FileExtractor
    {
        static FileExtractor()
        {
            ExtractResource("Wabbajack.Common.7z.dll.gz", "7z.dll");
            ExtractResource("Wabbajack.Common.7z.exe.gz", "7z.exe");
            ExtractResource("Wabbajack.Common.innounp.exe.gz", "innounp.exe");
        }

        private static void ExtractResource(string from, string to)
        {
            if (File.Exists(to))
                File.Delete(to);

            using (var ous = File.OpenWrite(to))
            using (var ins = new GZipInputStream(Assembly.GetExecutingAssembly().GetManifestResourceStream(from)))
            {
                ins.CopyTo(ous);
            }
        }


        public static void ExtractAll(WorkQueue queue, string source, string dest)
        {
            try
            {
                if (Consts.SupportedBSAs.Any(b => source.ToLower().EndsWith(b)))
                    ExtractAllWithBSA(queue, source, dest);
                else if (source.EndsWith(".omod"))
                    ExtractAllWithOMOD(source, dest);
                else if (source.EndsWith(".exe"))
                    ExtractAllWithInno(source, dest);
                else
                    ExtractAllWith7Zip(queue, source, dest);
            }
            catch (Exception ex)
            {
                queue.Log($"Error while extracting {source}");
                throw ex;
            }
        }

        private static void ExtractAllWithInno(string source, string dest)
        {
            Utils.Log($"Extracting {Path.GetFileName(source)}");

            var info = new ProcessStartInfo
            {
                FileName = "innounp.exe",
                Arguments = $"-x -y -b -d\"{dest}\" \"{source}\"",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = new Process {StartInfo = info};

            p.Start();
            ChildProcessTracker.AddProcess(p);

            try
            {
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e)
            {
                Utils.Error(e, "Error while setting process priority level for innounp.exe");
            }

            var name = Path.GetFileName(source);
            try
            {
                while (!p.HasExited)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (line == null)
                        break;

                    if (line.Length <= 4 || line[3] != '%')
                        continue;

                    int.TryParse(line.Substring(0, 3), out var percent);
                    Utils.Status($"Extracting {name} - {line.Trim()}", percent);
                }
            }
            catch (Exception e)
            {
                Utils.Error(e, "Error while reading StandardOutput for innounp.exe");
            }

            p.WaitForExit();
            if (p.ExitCode == 0)
                return;

            Utils.Log(p.StandardOutput.ReadToEnd());
            Utils.Log($"Extraction error extracting {source}");
        }

        private static string ExtractAllWithOMOD(string source, string dest)
        {
            Utils.Log($"Extracting {Path.GetFileName(source)}");
            var f = new Framework();
            f.SetTempDirectory(dest);
            var omod = new OMOD(source, ref f);
            omod.ExtractDataFiles();
            omod.ExtractPlugins();
            return dest;
        }

        private static void ExtractAllWithBSA(WorkQueue queue, string source, string dest)
        {
            try
            {
                using (var arch = BSADispatch.OpenRead(source))
                {
                    arch.Files
                        .PMap(queue, f =>
                        {
                            var path = f.Path;
                            if (f.Path.StartsWith("\\"))
                                path = f.Path.Substring(1);
                            Utils.Status($"Extracting {path}");
                            var outPath = Path.Combine(dest, path);
                            var parent = Path.GetDirectoryName(outPath);

                            if (!Directory.Exists(parent))
                                Directory.CreateDirectory(parent);

                            using (var fs = File.OpenWrite(outPath))
                            {
                                f.CopyDataTo(fs);
                            }
                        });
                }
            }
            catch (Exception ex)
            {
                queue.Log($"While Extracting {source}");
                throw ex;
            }
        }

        private static void ExtractAllWith7Zip(WorkQueue queue, string source, string dest)
        {
            queue.Log(new GenericInfo($"Extracting {Path.GetFileName(source)}", $"The contents of {source} are being extracted to {dest} using 7zip.exe"));

            var info = new ProcessStartInfo
            {
                FileName = "7z.exe",
                Arguments = $"x -bsp1 -y -o\"{dest}\" \"{source}\"",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = new Process {StartInfo = info};

            p.Start();
            ChildProcessTracker.AddProcess(p);
            try
            {
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
            }

            var name = Path.GetFileName(source);
            try
            {
                while (!p.HasExited)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (line == null)
                        break;

                    if (line.Length <= 4 || line[3] != '%') continue;

                    int.TryParse(line.Substring(0, 3), out var percent);
                    Utils.Status($"Extracting {name} - {line.Trim()}", percent);
                }
            }
            catch (Exception)
            {
            }

            p.WaitForExit();
            if (p.ExitCode == 0)
            {
                return;
            }
            queue.Log(new _7zipReturnError(p.ExitCode, source, dest, p.StandardOutput.ReadToEnd()));
        }

        /// <summary>
        ///     Returns true if the given extension type can be extracted
        /// </summary>
        /// <param name="v"></param>
        /// <returns></returns>
        public static bool CanExtract(string v)
        {
            var ext = Path.GetExtension(v.ToLower());
            if(ext != ".exe")
                return Consts.SupportedArchives.Contains(ext) || Consts.SupportedBSAs.Contains(ext);

            var info = new ProcessStartInfo
            {
                FileName = "innounp.exe",
                Arguments = $"-t \"{v}\" ",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = new Process {StartInfo = info};

            p.Start();
            ChildProcessTracker.AddProcess(p);

            var name = Path.GetFileName(v);
            while (!p.HasExited)
            {
                var line = p.StandardOutput.ReadLine();
                if (line == null)
                    break;

                if (line[0] != '#')
                    continue;

                Utils.Status($"Testing {name} - {line.Trim()}");
            }

            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }
}

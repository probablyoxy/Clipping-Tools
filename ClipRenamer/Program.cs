using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ClipRenamer
{
    class Program
    {
        private const string ClipRenamerVersion = "v0.4";

        static void Main(string[] args)
        {
            if (args.Length < 2) return;

            string renamerFolder = args[0];
            string clipFolder = args[1];

            string versionFile = Path.Combine(renamerFolder, "version.txt");
            try
            {
                File.WriteAllText(versionFile, ClipRenamerVersion);
            }
            catch { }

            string triggerFile = Path.Combine(renamerFolder, "exit_trigger.txt");
            string queueFile = Path.Combine(renamerFolder, "clip_queue.txt");
            string processingFile = Path.Combine(renamerFolder, "processing_queue.txt");
            string statusFile = Path.Combine(renamerFolder, "renamer_status.txt");

            void SendStatus(string msg)
            {
                DateTime timeout = DateTime.Now.AddSeconds(30);
                while (DateTime.Now < timeout)
                {
                    try
                    {
                        using (var fs = new FileStream(statusFile, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var writer = new StreamWriter(fs))
                        {
                            writer.Write(msg);
                        }
                        break;
                    }
                    catch { Thread.Sleep(500); }
                }
            }

            if (!File.Exists(triggerFile)) File.WriteAllText(triggerFile, "");
            DateTime lastWrite = new FileInfo(triggerFile).LastWriteTime;
            DateTime lastProcessCheck = DateTime.Now;

            HashSet<string> baselineFiles = new HashSet<string>();
            try
            {
                foreach (var f in Directory.GetFiles(clipFolder)) baselineFiles.Add(f);
            }
            catch { }

            while (true)
            {
                Thread.Sleep(500);

                try
                {
                    if ((DateTime.Now - lastProcessCheck).TotalSeconds >= 5)
                    {
                        lastProcessCheck = DateTime.Now;
                        if (Process.GetProcessesByName("ClippingTools").Length == 0)
                        {
                            break;
                        }
                    }

                    var fi = new FileInfo(triggerFile);
                    if (fi.LastWriteTime > lastWrite)
                    {
                        lastWrite = fi.LastWriteTime;
                        string content = File.ReadAllText(triggerFile).Trim();
                        if (content == "EXIT") break;
                    }

                    if (File.Exists(queueFile))
                    {
                        try
                        {
                            File.Move(queueFile, processingFile);
                        }
                        catch
                        {
                            continue;
                        }

                        string[] lines = File.ReadAllLines(processingFile);

                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            string[] parts = line.Split('|');
                            if (parts.Length < 2) continue;

                            string rawClipperName = parts[0].TrimStart('.', ' ');
                            string rawVcName = parts[1].TrimStart('.', ' ');

                            string clipperName = string.Join("_", rawClipperName.Split(Path.GetInvalidFileNameChars()));
                            string vcName = string.Join("_", rawVcName.Split(Path.GetInvalidFileNameChars()));

                            string prefix = $"{clipperName} - {vcName} - ";

                            string foundFile = null;
                            DateTime timeout = DateTime.Now.AddMinutes(1);

                            while (DateTime.Now < timeout)
                            {
                                try
                                {
                                    var currentFiles = Directory.GetFiles(clipFolder);
                                    string newestFile = null;
                                    DateTime newestTime = DateTime.MinValue;

                                    foreach (var f in currentFiles)
                                    {
                                        if (!baselineFiles.Contains(f) && !Path.GetFileName(f).StartsWith(prefix))
                                        {
                                            DateTime fileTime = File.GetLastWriteTime(f);
                                            if (newestFile == null || fileTime > newestTime)
                                            {
                                                newestFile = f;
                                                newestTime = fileTime;
                                            }
                                        }
                                    }

                                    if (newestFile != null && (DateTime.Now - newestTime).TotalSeconds <= 10)
                                    {
                                        foundFile = newestFile;
                                    }
                                }
                                catch { }

                                if (foundFile != null) break;
                                Thread.Sleep(500);
                            }

                            if (foundFile != null)
                            {
                                SendStatus("FOUND");

                                bool renamed = false;
                                DateTime renameTimeout = DateTime.Now.AddMinutes(2);

                                while (!renamed && DateTime.Now < renameTimeout)
                                {
                                    try
                                    {
                                        string newName = prefix + Path.GetFileName(foundFile);
                                        string newPath = Path.Combine(Path.GetDirectoryName(foundFile), newName);
                                        File.Move(foundFile, newPath);

                                        SendStatus($"RENAMED|{newName}");
                                        baselineFiles.Add(newPath);
                                        renamed = true;
                                    }
                                    catch
                                    {
                                        Thread.Sleep(500);
                                    }
                                }
                            }
                            else
                            {
                                SendStatus("TIMEOUT");
                            }

                            baselineFiles.Clear();
                            try
                            {
                                foreach (var f in Directory.GetFiles(clipFolder)) baselineFiles.Add(f);
                            }
                            catch { }
                        }

                        File.Delete(processingFile);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
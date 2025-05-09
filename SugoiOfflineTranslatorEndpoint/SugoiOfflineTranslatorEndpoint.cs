﻿using System;
using System.Linq;
using System.Collections;
using System.Text;
using System.Diagnostics;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Http;
using XUnity.AutoTranslator.Plugin.Core.Web;
using System.Reflection;
using System.IO;
using System.Net;
using System.Windows.Forms;
using XUnity.AutoTranslator.Plugin.Core;
using XUnity.Common.Logging;
using SugoiOfflineTranslator.SimpleJSON;
using System.Collections.Generic;

namespace SugoiOfflineTranslator
{
    public class SugoiOfflineTranslatorEndpoint : HttpEndpoint, ITranslateEndpoint, IDisposable, IMonoBehaviour_Update
    {
        public override string Id => "SugoiOfflineTranslator";
        public override string FriendlyName => "Sugoi Offline Translator";
        int maxTranslationsPerRequest { get; set; } = 100;
        public override int MaxTranslationsPerRequest => maxTranslationsPerRequest;

        private Process process;
        private bool isDisposing = false;
        private bool isStarted = false;
        private bool isReady = false;

        private string ServerScriptPath { get; set; }
        private string ServerExecPath { get; set; }
        private string Ct2ModelPath { get; set; }
        
        public readonly string[] pythonExePaths = 
        { 
          "Power-Source\\Python38\\python.exe",
          "Power-Source\\Python39\\python.exe"
        };

        public readonly string[] serverExecPaths =
        { "backendServer\\Program-Backend\\Sugoi-Translator-Offline\\offlineTranslation",
          "backendServer\\Program-Backend\\Sugoi-Japanese-Translator\\offlineTranslation",
          "backendServer\\Modules\\Translation-API-Server\\Offline\\Sugoi_Model"
        };

        public readonly string[] ct2Paths =
        {
          "ct2\\ct2_models",
          "models\\ct2Model",
          "ct2Model"
        };

        private string ServerPort { get; set; }
        private string SugoiInstallPath { get; set; }

        private bool UseExternalServer { get; set; } = false;
        
        private bool EnableCuda { get; set; }
        private bool EnableShortDelay { get; set; }
        private bool DisableSpamChecks { get; set; }
        private bool LogServerMessages { get; set; }
        private bool EnableCTranslate2 { get; set; }
        private string PythonExePath { get; set; }
        private bool HideServerWindow { get; set; }

        public override void Initialize(IInitializationContext context)
        {
            if (context.SourceLanguage != "ja") throw new Exception("Only ja is supported as source language");
            if (context.DestinationLanguage != "en") throw new Exception("Only en is supported as destination language");

            this.SugoiInstallPath = context.GetOrCreateSetting("SugoiOfflineTranslator", "InstallPath", "");
            this.ServerPort = context.GetOrCreateSetting("SugoiOfflineTranslator", "ServerPort", "14367");
            this.EnableCuda = context.GetOrCreateSetting("SugoiOfflineTranslator", "EnableCuda", false);
            this.maxTranslationsPerRequest = context.GetOrCreateSetting("SugoiOfflineTranslator", "MaxBatchSize", 10);
            this.ServerScriptPath = context.GetOrCreateSetting("SugoiOfflineTranslator", "CustomServerScriptPath", "");
            this.maxTranslationsPerRequest = context.GetOrCreateSetting("SugoiOfflineTranslator", "MaxBatchSize", 10);
            this.EnableShortDelay = context.GetOrCreateSetting("SugoiOfflineTranslator", "EnableShortDelay", false);
            this.DisableSpamChecks = context.GetOrCreateSetting("SugoiOfflineTranslator", "DisableSpamChecks", true);
            this.LogServerMessages = context.GetOrCreateSetting("SugoiOfflineTranslator", "LogServerMessages", false);
            this.EnableCTranslate2 = context.GetOrCreateSetting("SugoiOfflineTranslator", "EnableCTranslate2", false);
            this.HideServerWindow = context.GetOrCreateSetting("SugoiOfflineTranslator", "HideServerWindow", false);

            if (this.EnableShortDelay)
            {
                context.SetTranslationDelay(0.1f);
            }

            if (this.DisableSpamChecks)
            {
                context.DisableSpamChecks();
            }

            if (!string.IsNullOrEmpty(this.SugoiInstallPath))
            {
                this.SetupServer(context);
            } else
            {
                XuaLogger.AutoTranslator.Info($"Sugoi install path not configured. Either configure a path or start sugoi externally.");
                this.UseExternalServer = true;
                this.ServerPort = "14366";
                this.maxTranslationsPerRequest = 1;
            }
        }

        private string GetValidPath(string[] paths, string basePath, string description, bool optional=false)
        {
            string[] fullPaths = paths.Select(p => Path.Combine(basePath, p)).ToArray();
            var existing = fullPaths.Where(p => File.Exists(p) || Directory.Exists(p)).FirstOrDefault();
 
            if (existing != null)
            {
                return existing;
            }
            else
            {
                if (optional) return null;

                MessageBox.Show($"Failed to start Sugoi Offline Translator Server!\n\nUnable to find {description}!",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new Exception($"[Sugoi Offline Translator]] Unable to find {description}!");
            }
        }
        private void SetupServer(IInitializationContext context)
        {
            var configuredEndpoint = context.GetOrCreateSetting<string>("Service", "Endpoint");

            if (configuredEndpoint == this.Id)
            {

                this.PythonExePath = GetValidPath(this.pythonExePaths, this.SugoiInstallPath, "Python power-source");

                this.ServerExecPath = GetValidPath(this.serverExecPaths, this.SugoiInstallPath, "translation server working directory (model directory)");

                // Fairseq model checks
                string fairseqModelPath = Path.Combine(this.ServerExecPath, "fairseq");
                string fairseqModelFile = Directory.Exists(fairseqModelPath) ? Path.Combine(fairseqModelPath, "japaneseModel\\big.pretrain.pt") : null;
                string fairseqSPMFile = Directory.Exists(fairseqModelPath) ? Path.Combine(fairseqModelPath, "spmModels\\spm.ja.nopretok.model") : null;

                if (Directory.Exists(fairseqModelPath))
                {
                    XuaLogger.AutoTranslator.Info($"[Sugoi Offline Translator] Fairseq Model Info\n\t" +
                        $"Path: {fairseqModelPath}\n\t" +
                        $"Model File Found: {File.Exists(fairseqModelFile)}\n\t" +
                        $"SPM File Found: {File.Exists(fairseqSPMFile)}");
                }

                //CT2 model checks
            
                this.Ct2ModelPath = GetValidPath(this.ct2Paths, this.ServerExecPath, "CT2 model path", true);
                string ct2SPMSource = !string.IsNullOrEmpty(this.Ct2ModelPath) ? Path.Combine(this.Ct2ModelPath, "..\\spmModels\\spm.ja.nopretok.model") : "";
                string ct2SPMTarget = !string.IsNullOrEmpty(this.Ct2ModelPath) ? Path.Combine(this.Ct2ModelPath, "..\\spmModels\\spm.en.nopretok.model") : "";

                if (Directory.Exists(Ct2ModelPath))
                {
                    XuaLogger.AutoTranslator.Info($"[Sugoi Offline Translator] Ctranslate2 Model Info\n\t" +
                        $"Path: {Ct2ModelPath}\n\t" +
                        $"Source SPM File Found: {File.Exists(ct2SPMSource)}\n\t" +
                        $"Target SPM File Found: {File.Exists(ct2SPMTarget)}");
                }

                // Check if any model available
                if ((string.IsNullOrEmpty(this.Ct2ModelPath) || !File.Exists(ct2SPMTarget) || !File.Exists(ct2SPMSource)) && (!Directory.Exists(fairseqModelPath) || !File.Exists(fairseqSPMFile)))
                {
                    MessageBox.Show("Failed to start Sugoi Offline Translator Server!\n\nUnable to find any translation models!" +
                        "\n\nCheck your Sugoi Toolkit Installation!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    throw new Exception("[Sugoi Offline Translator] Unable to find any model files!");
                }

                // Fairseq failover
                if (EnableCTranslate2 && (!File.Exists(ct2SPMSource) || !File.Exists(ct2SPMTarget)))
                {
                    MessageBox.Show("[Sugoi Offline Translator Plugin]\n\nWarning!\n\nCTranslate2 model not found or incomplete!" +
                    "\n\nFalling back to the Fairseq model!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    context.SetSetting("SugoiOfflineTranslator", "EnableCTranslate2", false);
                    this.EnableCTranslate2 = false;
                    XuaLogger.AutoTranslator.Warn("[Sugoi Offline Translator] CTranslate2 model not found or incomplete! Falling back to Fairseq!");
                }

                // CT2 failover
                if (!EnableCTranslate2 && (!File.Exists(fairseqModelFile) || !File.Exists(fairseqSPMFile)))
                {
                     MessageBox.Show("[Sugoi Offline Translator Plugin]\n\nWarning!\n\nFairseq model not found or incomplete!" +
                     "\n\nEnabling Ctranslate2 model!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                     context.SetSetting("SugoiOfflineTranslator", "EnableCTranslate2", true);
                     this.EnableCTranslate2 = true;
                     XuaLogger.AutoTranslator.Warn("[Sugoi Offline Translator] Fairseq model not found! Enabling CT2!");
                }

                if (string.IsNullOrEmpty(this.ServerScriptPath))
                {
                    var tempPath = Path.GetTempPath();
                    this.ServerScriptPath = Path.Combine(tempPath, "SugoiOfflineTranslatorServer.py");
                    File.WriteAllBytes(this.ServerScriptPath, Properties.Resources.SugoiOfflineTranslatorServer);
                }

                this.StartProcess();
            }
        }

        public void Dispose()
        {
            this.isDisposing = true;
            if (this.process != null)
            {
                this.process.Kill();
                this.process.Dispose();
                this.process = null;
            }
        }

        private void StartProcess()
        {
            if (this.process == null || this.process.HasExited)
            {
                string cuda = this.EnableCuda ? "--cuda" : "";
                string ctranslate = this.EnableCTranslate2 ? "--ctranslate2" : "";
                string ct2_model_path = this.EnableCTranslate2 ? $"--ctranslate2-data-dir \"{this.Ct2ModelPath}\"" : "";

                XuaLogger.AutoTranslator.Info($"Running Sugoi Offline Translation server:\n\t" +
                    $"ExecPath: {this.ServerExecPath}\n\t" +
                    $"PythonPath: {this.PythonExePath}\n\t" +
                    $"ScriptPath: {this.ServerScriptPath}\n\t" +
                    $"Translation Model: {(this.EnableCTranslate2 ? "CT2" : "Fairseq")}\n\t" +
                    $"CUDA: {(this.EnableCuda ? "Enabled" : "Disabled")}");

                this.process = new Process();
                this.process.StartInfo = new ProcessStartInfo()
                {
                    FileName = this.PythonExePath,
                    Arguments = $"\"{this.ServerScriptPath}\" {this.ServerPort} {cuda} {ctranslate} {ct2_model_path}",
                    WorkingDirectory = this.ServerExecPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = this.HideServerWindow
                };

                this.process.OutputDataReceived += this.ServerDataReceivedEventHandler;
                this.process.ErrorDataReceived += this.ServerDataReceivedEventHandler;

                this.process.Start();
                this.process.BeginErrorReadLine();
                this.process.BeginOutputReadLine();
                this.isStarted = true;
            }
        }

        void ServerDataReceivedEventHandler(object sender, DataReceivedEventArgs args)
        {
            if (this.LogServerMessages)
            {
                XuaLogger.AutoTranslator.Info(args.Data);
            }

            if (!this.isReady && args.Data.Contains("(Press CTRL+C to quit)"))
            {
                this.isReady = true;
            }
        }

        IEnumerator ITranslateEndpoint.Translate(ITranslationContext context)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var iterator = base.Translate(context);

            while (iterator.MoveNext()) yield return iterator.Current;
            
            var elapsed = stopwatch.Elapsed.TotalSeconds;

            if(LogServerMessages)
            {
                XuaLogger.AutoTranslator.Info($"Translate complete {elapsed}s");

            }
        }

        public override IEnumerator OnBeforeTranslate(IHttpTranslationContext context)
        {
            if (this.UseExternalServer)
            {
                yield break;
            }

            if (this.isStarted && this.process.HasExited)
            {
                this.isStarted = false;
                this.isReady = false;

                XuaLogger.AutoTranslator.Warn($"Translator server process exited unexpectedly [status {process.ExitCode}]");
            }

            if (!this.isStarted && !this.isDisposing)
            {
                XuaLogger.AutoTranslator.Warn($"Translator server process not running. Starting...");
                this.StartProcess();
            }

            while (!isReady) yield return null;
        }

        public string GetUrlEndpoint()
        {
            return $"http://127.0.0.1:{ServerPort}/";
        }

        public override void OnCreateRequest(IHttpRequestCreationContext context)
        {
            var json = new JSONObject();

            if (!this.UseExternalServer)
            {
                json["content"] = context.UntranslatedText;
                json["batch"] = context.UntranslatedTexts;
                json["message"] = "translate batch";
            }
            else
            {
                json["content"] = context.UntranslatedText;
                json["message"] = "translate sentences";
            }

            var data = json.ToString();

            var request = new XUnityWebRequest("POST", GetUrlEndpoint(), data);
            request.Headers["Content-Type"] = "application/json";
            request.Headers["Accept"] = "*/*";

            context.Complete(request);
        }

        public override void OnExtractTranslation(IHttpTranslationExtractionContext context)
        {
            var data = context.Response.Data;
            var result = JSON.Parse(data);
            
            if (!UseExternalServer)
            {
                context.Complete(result.AsStringList.ToArray());
            }
            else
            {
                if (result.IsString)
                {
                    context.Complete(result.Value);
                }
                else
                {
                    context.Fail($"Unexpected return from server: {data}");
                }
            }
        }

        
        public void Update()
        {
            //XuaLogger.AutoTranslator.Info($"Sugoi update!");
        }
    }
}

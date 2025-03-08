using System;
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

namespace SugoiOfflineTranslator
{
    public class SugoiOfflineTranslatorEndpoint : HttpEndpoint, ITranslateEndpoint, IDisposable, IMonoBehaviour_Update
    {
        public override string Id => "SugoiOfflineTranslator";

        public override string FriendlyName => "Sugoi Offline Translator";

        //public override int MaxConcurrency => 1;
        int maxTranslationsPerRequest { get; set; } = 100;

        public override int MaxTranslationsPerRequest => maxTranslationsPerRequest;

        private Process process;
        private bool isDisposing = false;
        private bool isStarted = false;
        private bool isReady = false;

        private string ServerScriptPath { get; set; }
        private string ServerExecPath { get; set; }
        private string Ct2ModelPath { get; set; }

        //private string ServerPort => 14366;
        private string ServerPort { get; set; }

        //private string SugoiInstallPath => "G:\\Downloads\\Sugoi-Japanese-Translator-V3.0";
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

        private void SetupServer(IInitializationContext context)
        {
            var pythonExePathCandidates = new string[]
            {
                Path.Combine(this.SugoiInstallPath, "Power-Source\\Python38\\python.exe"),
                Path.Combine(this.SugoiInstallPath, "Power-Source\\Python39\\python.exe"),
            };

            this.PythonExePath = pythonExePathCandidates.Where(p => File.Exists(p)).FirstOrDefault();
            if (string.IsNullOrEmpty(this.PythonExePath))
            {
                MessageBox.Show("Failed to start Sugoi Offline Translator Server!\n\nUnable to find Python runtime!" +
                    "\n\nCheck if you set the InstallPath correctly!\nIf yes, report this error to the author!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new Exception("[Sugoi Offline Translator] Unable to find Python power-source! (Python3x folder)");
            }

            var pythonServerExecPathCandidates = new string[]
            {
                Path.Combine(this.SugoiInstallPath, "backendServer\\Program-Backend\\Sugoi-Translator-Offline\\offlineTranslation"),
                Path.Combine(this.SugoiInstallPath, "backendServer\\Program-Backend\\Sugoi-Japanese-Translator\\offlineTranslation"),
                Path.Combine(this.SugoiInstallPath, "backendServer\\Modules\\Translation-API-Server\\Offline\\Sugoi_Model")
            };

            this.ServerExecPath = pythonServerExecPathCandidates.Where(p => Directory.Exists(p)).FirstOrDefault();

            if (string.IsNullOrEmpty(this.ServerExecPath))
            {
                MessageBox.Show("Failed to start Sugoi Offline Translator Server!\n\nUnable to find translation server working directory (model directory)!" +
                    "\n\nCheck if you set the InstallPath correctly!\nIf yes, report this error to the author!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new Exception("[Sugoi Offline Translator] Unable to find translation server working directory (model directory)!");
            }

            var ct2ModelPathCandidates = new string[]
            {
                Path.Combine(this.ServerExecPath, "ct2\\ct2_models"),
                Path.Combine(this.ServerExecPath, "models\\ct2Model"),
                Path.Combine(this.ServerExecPath, "ct2Model"),
            };

            this.Ct2ModelPath = ct2ModelPathCandidates.Where(p => Directory.Exists(p)).FirstOrDefault();
            if (!string.IsNullOrEmpty(this.Ct2ModelPath)) XuaLogger.AutoTranslator.Info($"[Sugoi Offline Translator] CT2 Model Path: {this.Ct2ModelPath}");

            string fairseqModelPath = Path.Combine(this.ServerExecPath, "fairseq");
            if (!string.IsNullOrEmpty(fairseqModelPath)) XuaLogger.AutoTranslator.Info($"[Sugoi Offline Translator] Fairseq model path: {fairseqModelPath}");

            if (EnableCTranslate2 && string.IsNullOrEmpty(this.Ct2ModelPath))
            {
                if (Directory.Exists(fairseqModelPath))
                {
                    MessageBox.Show("[Sugoi Offline Translator Plugin]\n\nWarning!\n\nCTranslate2 model not found!" +
                    "\n\nFalling back to the Fairseq model!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    context.SetSetting("SugoiOfflineTranslator", "EnableCTranslate2", false);
                    this.EnableCTranslate2 = false;
                    XuaLogger.AutoTranslator.Warn("[Sugoi Offline Translator Plugin] CTranslate2 model not found! Falling back to Fairseq!");
                }
            }

            if (!EnableCTranslate2 && !Directory.Exists(fairseqModelPath))
            {
                 MessageBox.Show("[Sugoi Offline Translator Plugin]\n\nWarning!\n\nFairseq model not found!" +
                 "\n\nEnabling Ctranslate2 model!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                 context.SetSetting("SugoiOfflineTranslator", "EnableCTranslate2", true);
                 this.EnableCTranslate2 = true;
                 XuaLogger.AutoTranslator.Warn("[Sugoi Offline Translator Plugin] Fairseq model not found! Enabling CT2!");
            }

            if (string.IsNullOrEmpty(this.ServerScriptPath))
            {
                var tempPath = Path.GetTempPath();
                this.ServerScriptPath = Path.Combine(tempPath, "SugoiOfflineTranslatorServer.py");
                File.WriteAllBytes(this.ServerScriptPath, Properties.Resources.SugoiOfflineTranslatorServer);
            }

            var configuredEndpoint = context.GetOrCreateSetting<string>("Service", "Endpoint");
            if (configuredEndpoint == this.Id)
            {
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
                string ct2_model_path = this.EnableCTranslate2 ? $"--ctranslate2-data-dir {this.Ct2ModelPath}" : "";

                XuaLogger.AutoTranslator.Info($"Running Sugoi Offline Translation server:\n\tExecPath: {this.ServerExecPath}\n\tPythonPath: {this.PythonExePath}\n\tScriptPath: {this.ServerScriptPath}");

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

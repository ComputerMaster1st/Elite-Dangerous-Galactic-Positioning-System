using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdGps.Core.Models;

namespace EdGps.Core
{
    public class JournalReader
    {
        private DirectoryInfo _directory;
        private CancellationTokenSource _cancelReader = null;
        private FileSystemWatcher watcher = new FileSystemWatcher();
        private Task _task = null;
        private ReaderStatus _status = ReaderStatus.Idle;
        private bool _isReady = false;

        public event EventHandler<FsdJump> OnFsdJump;
        public event EventHandler<FssDiscoveryScan> OnFssDiscoveryScan;
        public event EventHandler<Body> OnBodyScan;
        public event EventHandler<DssScan> OnDssScan;
        public event EventHandler<bool> OnAllBodiesFound;
        public event EventHandler<StartJump> OnStartJump;
        public event EventHandler<bool> OnShutdown;
        public event EventHandler<bool> OnReady;

        public JournalReader(string journalDirectory) {
            _directory = new DirectoryInfo(journalDirectory);

            watcher.Path = journalDirectory;
            watcher.NotifyFilter = NotifyFilters.LastAccess 
                | NotifyFilters.LastWrite
                | NotifyFilters.FileName;
            watcher.Filter = "*.log";
            watcher.Created += OnCreatedAsync;
        }

        public void Start() {
            _cancelReader?.Dispose();
            _cancelReader = new CancellationTokenSource();            
            watcher.EnableRaisingEvents = true;
            _task = Task.Run(async () => await RunAsync(GetJournal()));
        }

        private async Task StopAsync() {
            _cancelReader.Cancel();
            while (_status != ReaderStatus.Stopped) await Task.Delay(1000);
        }

        private FileInfo GetJournal()
            => _directory.GetFiles()
                .Where(f => f.Extension == ".log")
                .OrderByDescending(f => f.Name)
                .First();

        public void Build() {
            var journalFiles = _directory.GetFiles()
                .Where(f => f.Extension == ".log")
                .OrderBy(f => f.Name);
            Directory.CreateDirectory(Directories.SystemDir);

            foreach (var journal in journalFiles) {
                using (FileStream fs = journal.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                    while (!sr.EndOfStream)
                        ReadEvent(Parser.ParseJson(sr.ReadLine()));
            }
        }

        private async Task RunAsync(FileInfo journalFile) {
            using (FileStream fs = journalFile.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) 
            using (StreamReader sr = new StreamReader(fs)) {
                _status = ReaderStatus.Active;
                while (!_cancelReader.IsCancellationRequested) {
                    while (!sr.EndOfStream) ReadEvent(Parser.ParseJson(sr.ReadLine()));
                    while (sr.EndOfStream && !_cancelReader.IsCancellationRequested) {
                        if (!_isReady) {
                            _isReady = true;
                            OnReady?.Invoke(this, _isReady);
                        }
                        await Task.Delay(1000);
                    }
                }
            }
            _status = ReaderStatus.Stopped;
        }

        private async void OnCreatedAsync(object sender, FileSystemEventArgs e) {
            await StopAsync();
            Start();
        }

        public void ReadEvent(Dictionary<string, object> rawData) {
            if (!rawData.ContainsKey("event")) return;

            switch (rawData["event"]) {
                case "FSDJump":
                    OnFsdJump?.Invoke(this, Parser.ParseJournalEvent<FsdJump>(rawData));
                    break;
                case "FSSDiscoveryScan":
                    OnFssDiscoveryScan?.Invoke(this, Parser.ParseJournalEvent<FssDiscoveryScan>(rawData));
                    break;
                case "Scan":
                    var body = Parser.ParseScanBody(rawData);

                    if (rawData.ContainsKey("StarType")) {
                        var subType = (string)rawData["StarType"];
                        body.Type = Parser.ParseStarType(subType);
                        body.SubType = subType;
                        body.Mass = (double)rawData["StellarMass"];
                    } else if (rawData.ContainsKey("PlanetClass")) {
                        var subType = (string)rawData["PlanetClass"];
                        body.Type = Parser.ParseWorldType(subType);
                        body.SubType = subType;
                        body.Terraformable = rawData.ContainsKey("TerraformState") ? (string)rawData["TerraformState"] : string.Empty;
                        body.Mass = (double)rawData["MassEM"];
                    } else body.Type = BodyType.Belt;

                    OnBodyScan?.Invoke(this, body);
                    break;
                case "FSSAllBodiesFound":
                    OnAllBodiesFound?.Invoke(this, true);
                    break;
                case "SAAScanComplete":
                    OnDssScan?.Invoke(this, Parser.ParseJournalEvent<DssScan>(rawData));
                    break;
                case "StartJump":
                    OnStartJump?.Invoke(this, Parser.ParseJournalEvent<StartJump>(rawData));
                    break;
                case "Shutdown":
                    OnShutdown?.Invoke(this, true);
                    break;
                default:
                    return;
            }
        }
    }
}
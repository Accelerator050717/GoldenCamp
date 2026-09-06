using System;
using System.IO;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Development-only control of this CLIENT. Gameplay still crosses FishNet.
    // Neither this driver nor the external process harness can invoke host rules.
    public sealed class CampTestDriver : MonoBehaviour
    {
        private CampSession _session;
        private string _root;
        private float _nextPoll;
        private int _consumed;
        private int _sequence;
        private float _nextInput;
        public void Initialize(CampSession session)
        {
            if (!Debug.isDebugBuild || !session.IsTestSession) { Destroy(this); return; }
            _session = session; _root = session.TestRoot;
            session.CommandResult += Result;
            session.ViewChanged += Snapshot;
            File.WriteAllText(Path.Combine(_root, "process.json"), "{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
        }
        private void Snapshot(CampView view) => File.WriteAllText(Path.Combine(_root, "view.json"), JsonUtility.ToJson(view));
        private void Result(CampResult result) => File.AppendAllText(Path.Combine(_root, "results.jsonl"), JsonUtility.ToJson(result) + "\n");
        private void Update()
        {
            if (_session != null && Time.unscaledTime >= _nextInput)
            {
                _nextInput = Time.unscaledTime + .05f;
                string inputPath = Path.Combine(_root, "input.json");
                if (File.Exists(inputPath))
                {
                    try
                    {
                        // The harness atomically replaces input frames while this client reads.
                        using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        var input = JsonUtility.FromJson<CampInputFrame>(reader.ReadToEnd());
                        if (input != null) { input.Sequence = ++_sequence; _session.SendInput(input); }
                    }
                    catch (IOException) { }
                    catch (ArgumentException) { }
                }
            }
            if (_session == null || Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + .1f;
            string inbox = Path.Combine(_root, "inbox.jsonl");
            if (!File.Exists(inbox)) return;
            string[] lines;
            try { lines = File.ReadAllLines(inbox); } catch (IOException) { return; }
            while (_consumed < lines.Length)
            {
                string line = lines[_consumed];
                if (string.IsNullOrWhiteSpace(line)) { _consumed++; continue; }
                CampCommand command;
                try { command = JsonUtility.FromJson<CampCommand>(line); }
                catch (ArgumentException) { return; }
                _consumed++;
                if (command.Action == "shutdown") _session.EndSession(true);
                else _session.SendCommand(command);
            }
        }
        private void OnDestroy()
        {
            if (_session != null) { _session.CommandResult -= Result; _session.ViewChanged -= Snapshot; }
        }
    }
}

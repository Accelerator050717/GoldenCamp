using System;
using System.IO;
using System.Linq;
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
        private void Snapshot(CampView view)
        {
            // A diagnostics reader may briefly hold this test-only file. Never
            // let observer I/O escape into FishNet's gameplay event dispatch.
            // The next view retries the latest complete state; persistent I/O
            // failure remains visible to the harness as a stale snapshot.
            try { File.WriteAllText(Path.Combine(_root, "view.json"), JsonUtility.ToJson(view)); }
            catch (IOException) { }
        }
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
                else if (command.Action == "voice_test")
                {
                    var voice = _session.GetComponent<CampVoice>();
                    bool valid = voice != null && (command.TargetId == "off" || command.TargetId == "tone" || command.TargetId == "mic");
                    if (valid) voice.SetTestMode(command.TargetId, command.OtherPlayerId);
                    Result(new CampResult { RequestId = command.RequestId, Success = valid,
                        Code = valid ? "ok" : "voice_mode", Message = "Development local audio control",
                        Revision = _session.View?.Revision ?? 0 });
                }
                else _session.SendCommand(command);
            }
        }
        private void OnDestroy()
        {
            if (_session != null) { _session.CommandResult -= Result; _session.ViewChanged -= Snapshot; }
        }

        // A marked Development capacity fixture, never a normal gameplay route.
        // Original finite yields remain intact; the fixture models a long-lived
        // camp with every cell excavated, supported ladders, and unsold cargo.
        public static void FillMineCapacity(CampAuthority authority, string contributor)
        {
            if (!Debug.isDebugBuild) throw new InvalidOperationException("Development fixture only.");
            authority.InitializeMining(); authority.InitializeProgression();
            var state = CampCodec.Clone(authority.State);
            if (state.Cells.Any(cell => cell.Dug) || state.Ladders.Count != 0)
                throw new InvalidOperationException("Capacity fixture requires a fresh mine.");
            state.RoadCoinsPaid = state.RoadRequiredCoins; state.RoadIronPaid = state.RoadRequiredIron;
            state.RoadUnlocked = true; state.RoadVersion = 2;
            state.Contributions.Add(new CampContribution { PlayerId = contributor,
                Coins = state.RoadRequiredCoins, Iron = state.RoadRequiredIron });
            foreach (var player in state.Players) { player.ToolLevel = 2; player.LadderStock = 0; }
            foreach (var cell in state.Cells)
            {
                cell.Dug = true; cell.Health = 0; cell.Version = 10;
                float height = cell.YieldKind == ItemKind.HeavyOre ? .65f : .35f;
                for (int i = 0; i < cell.YieldCount; i++)
                    state.Items.Add(new CampItem { Id = "yield:" + cell.Id + ":" + i, Kind = cell.YieldKind,
                        Place = ItemPlace.Ground, Position = CampMap.CellCenter(cell, authority.Rules) + Vector3.up * (height - authority.Rules.CellSize * .5f),
                        HiddenValue = cell.HiddenValue, CoreKind = cell.CoreKind, RawClueBand = cell.RawClueBand, RawWindowBand = cell.RawWindowBand });
                state.Ladders.Add(new CampLadder { Id = "ladder:" + cell.Id, CellId = cell.Id, PitId = cell.PitId,
                    X = cell.X, Depth = cell.Depth, Z = cell.Z, Version = 1 });
            }
            CampAuthority.Validate(state); authority.Commit(state);
        }
    }
}

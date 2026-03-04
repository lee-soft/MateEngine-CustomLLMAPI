using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json;
using CustomLLMAPI;

/// <summary>
/// PuppetMaster — MonoBehaviour orchestrator.
///
/// Owns the main-thread dispatch queue and wires together:
///   • <see cref="PuppetMasterActions"/>  — pure avatar control (LLM-callable)
///   • <see cref="PuppetMasterHttpServer"/> — HTTP/socket layer (web UI + REST)
///
/// LLM integration: obtain a reference to <see cref="Actions"/> and call its
/// methods directly from the Unity main thread, or use
/// <see cref="EnqueueOnMainThread"/> to dispatch from a background thread.
/// </summary>
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// PuppetMaster — MonoBehaviour orchestrator.
///
/// Owns the main-thread dispatch queue and wires together:
///   • <see cref="PuppetMasterActions"/>  — pure avatar control (LLM-callable)
///   • <see cref="PuppetMasterHttpServer"/> — HTTP/socket layer (web UI + REST)
///
/// LLM integration: obtain a reference to <see cref="Actions"/> and call its
/// methods directly from the Unity main thread, or use
/// <see cref="EnqueueOnMainThread"/> to dispatch from a background thread.
/// </summary>
public class PuppetMaster : MonoBehaviour
{
    [Header("Server Settings")]
    public int puppetPort = 13335;
    public bool startOnAwake = true;

    /// <summary>
    /// Direct access to all avatar controls.
    /// Safe to call from the LLM on the Unity main thread.
    /// </summary>
    public PuppetMasterActions Actions { get; private set; }

    private PuppetMasterHttpServer _httpServer;

    private readonly List<Action> _mainQueue = new List<Action>();
    private readonly object _queueLock = new object();

    // ── Unity lifecycle ───────────────────────────────────────────────────

    void Awake()
    {
        Actions = gameObject.AddComponent<PuppetMasterActions>();

        _httpServer = new PuppetMasterHttpServer(puppetPort, Actions, EnqueueOnMainThread);

        if (startOnAwake) StartServer();
    }

    void Update()
    {
        List<Action> toRun = null;
        lock (_queueLock)
        {
            if (_mainQueue.Count > 0)
            {
                toRun = new List<Action>(_mainQueue);
                _mainQueue.Clear();
            }
        }
        if (toRun == null) return;
        foreach (var cmd in toRun)
        {
            try { cmd(); }
            catch (Exception ex) { Debug.LogError("[PuppetMaster] Command error: " + ex.Message); }
        }
    }

    void OnDestroy() => StopServer();

    // ── Server control ────────────────────────────────────────────────────

    public void StartServer()
    {
        Actions.Initialise();
        _httpServer.Start();
    }

    public void StopServer() => _httpServer.Stop();

    // ── Main-thread dispatch ──────────────────────────────────────────────

    /// <summary>
    /// Marshals <paramref name="work"/> onto the Unity main thread, blocks the
    /// calling thread until it completes, and returns the string result.
    ///
    /// Use this when calling <see cref="Actions"/> from a background thread.
    /// </summary>
    public string EnqueueOnMainThread(Func<string> work)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
        lock (_queueLock)
        {
            _mainQueue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetResult("ERROR: " + ex.Message); }
            });
        }
        return tcs.Task.GetAwaiter().GetResult();
    }
}






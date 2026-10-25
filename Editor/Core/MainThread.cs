using System;
using System.Collections.Concurrent;
using UnityEditor;

namespace Zetta3D.Editor
{
    /// <summary>Runs work queued from background threads on the Editor's main thread.</summary>
    [InitializeOnLoad]
    public static class MainThread
    {
        static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        static MainThread()
        {
            EditorApplication.update += Drain;
        }

        public static void Post(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }
    }
}

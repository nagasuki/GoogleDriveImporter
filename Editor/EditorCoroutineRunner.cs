#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GoogleDriveImporter
{
    internal static class EditorCoroutineRunner
    {
        private sealed class State
        {
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public object CurrentYield;
            public Action Completed;
            public Action<Exception> Failed;
        }

        private static readonly List<State> Running = new List<State>();
        private static bool isInitialized;

        public static void Start(IEnumerator routine, Action completed = null, Action<Exception> failed = null)
        {
            if (routine == null)
                throw new ArgumentNullException(nameof(routine));

            Initialize();
            var state = new State
            {
                Completed = completed,
                Failed = failed
            };
            state.Stack.Push(routine);
            Running.Add(state);
        }

        private static void Initialize()
        {
            if (isInitialized)
                return;

            EditorApplication.update += Update;
            isInitialized = true;
        }

        private static void Update()
        {
            for (int index = Running.Count - 1; index >= 0; index--)
            {
                State state = Running[index];
                bool completed = false;
                Exception failure = null;
                try
                {
                    completed = !Advance(state);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (!completed && failure == null)
                    continue;

                Running.RemoveAt(index);
                if (failure != null)
                    state.Failed?.Invoke(failure);
                else
                    state.Completed?.Invoke();
            }

            if (Running.Count == 0 && isInitialized)
            {
                EditorApplication.update -= Update;
                isInitialized = false;
            }
        }

        private static bool Advance(State state)
        {
            while (true)
            {
                if (state.CurrentYield != null)
                {
                    if (!IsReady(state.CurrentYield))
                        return true;
                    state.CurrentYield = null;
                }

                if (state.Stack.Count == 0)
                    return false;

                IEnumerator current = state.Stack.Peek();
                if (!current.MoveNext())
                {
                    state.Stack.Pop();
                    continue;
                }

                object yielded = current.Current;
                if (yielded is IEnumerator nested)
                {
                    state.Stack.Push(nested);
                    continue;
                }

                if (yielded == null)
                    return true;

                if (!IsReady(yielded))
                    state.CurrentYield = yielded;

                return true;
            }
        }

        private static bool IsReady(object yielded)
        {
            if (yielded is AsyncOperation operation)
                return operation.isDone;
            if (yielded is CustomYieldInstruction customYieldInstruction)
                return !customYieldInstruction.keepWaiting;
            if (yielded is YieldInstruction)
                return false;
            return true;
        }
    }
}
#endif

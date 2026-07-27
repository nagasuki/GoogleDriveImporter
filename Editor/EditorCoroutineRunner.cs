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

        public static IEnumerator RunInParallel(IList<IEnumerator> routines, int maxConcurrent)
        {
            if (routines == null)
                throw new ArgumentNullException(nameof(routines));
            if (maxConcurrent <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrent));

            return new ParallelRoutine(routines, maxConcurrent);
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

        private sealed class ParallelRoutine : IEnumerator
        {
            private sealed class ChildState
            {
                public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
                public object CurrentYield;
            }

            private readonly IList<IEnumerator> routines;
            private readonly List<ChildState> children = new List<ChildState>();
            private readonly int maxConcurrent;
            private int nextRoutineIndex;
            private Exception failure;

            public ParallelRoutine(IList<IEnumerator> routines, int maxConcurrent)
            {
                this.routines = routines;
                this.maxConcurrent = maxConcurrent;
                for (int index = 0; index < routines.Count; index++)
                {
                    if (routines[index] == null)
                        throw new ArgumentException("Parallel routines cannot contain null entries.", nameof(routines));
                }

                StartNextRoutines();
            }

            public object Current
            {
                get { return null; }
            }

            public bool MoveNext()
            {
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    ChildState child = children[index];
                    try
                    {
                        if (Advance(child))
                            continue;

                        children.RemoveAt(index);
                    }
                    catch (Exception exception)
                    {
                        children.RemoveAt(index);
                        if (failure == null)
                            failure = exception;
                    }
                }

                if (failure == null)
                    StartNextRoutines();

                if (children.Count > 0)
                    return true;

                if (failure != null)
                    throw failure;

                return false;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            private void StartNextRoutines()
            {
                while (failure == null
                    && nextRoutineIndex < routines.Count
                    && children.Count < maxConcurrent)
                {
                    var child = new ChildState();
                    child.Stack.Push(routines[nextRoutineIndex]);
                    children.Add(child);
                    nextRoutineIndex++;
                }
            }

            private static bool Advance(ChildState state)
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
        }
    }
}
#endif

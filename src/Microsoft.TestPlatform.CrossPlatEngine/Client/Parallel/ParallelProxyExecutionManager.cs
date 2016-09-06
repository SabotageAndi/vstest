// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Client.Parallel
{
    using System;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.Collections;
    using System.Linq;

    /// <summary>
    /// ParallelProxyExecutionManager that manages parallel execution
    /// </summary>
    internal class ParallelProxyExecutionManager : ParallelOperationManager<IProxyExecutionManager>, IParallelProxyExecutionManager
    {
        #region TestRunSpecificData

        private int runCompletedClients = 0;

        private TestRunCriteria actualTestRunCriteria;

        private IEnumerator<string> sourceEnumerator;

        private IEnumerator testCaseListEnumerator;

        private bool hasSpecificTestsRun = false;

        private Task lastParallelRunCleanUpTask = null;

        private IDictionary<IProxyExecutionManager, ITestRunEventsHandler> concurrentManagerHandlerMap;

        #endregion

        #region Concurrency Keeper Objects

        /// <summary>
        /// LockObject to iterate our sourceEnumerator in parallel
        /// We can use the sourceEnumerator itself as lockObject, but since its a changing object - it's risky to use it as one
        /// </summary>
        private object sourceEnumeratorLockObject = new object();

        /// <summary>
        /// LockObject to update execution status in parallel
        /// </summary>
        private object executionStatusLockObject = new object();

        #endregion

        public ParallelProxyExecutionManager(Func<IProxyExecutionManager> actualProxyManagerCreator, int parallelLevel)
            : base(actualProxyManagerCreator, parallelLevel)
        {
        }

        #region IProxyExecutionManager

        public void Initialize(ITestHostManager testHostManager)
        {
            DoActionOnAllManagers((proxyManager) => proxyManager.Initialize(testHostManager), doActionsInParallel: true);
        }

        public int StartTestRun(TestRunCriteria testRunCriteria, ITestRunEventsHandler eventHandler)
        {
            this.hasSpecificTestsRun = testRunCriteria.HasSpecificTests;
            this.actualTestRunCriteria = testRunCriteria;

            if (hasSpecificTestsRun)
            {
                var testCasesBySource = new Dictionary<string, List<TestCase>>();
                foreach (var test in testRunCriteria.Tests)
                {
                    if (!testCasesBySource.ContainsKey(test.Source))
                    {
                        testCasesBySource.Add(test.Source, new List<TestCase>());
                    }
                    testCasesBySource[test.Source].Add(test);
                }

                // Do not use "Dictionary.ValueCollection.Enumerator" - it becomes undetermenstic once we go out of scope of this method
                // Use "ToArray" to copy ValueColleciton to a simple array and use it's enumerator
                // Set the enumerator for parallel yielding of testCases
                // Whenever a concurrent executor becomes free, it picks up the next set of testCases using this enumerator
                this.testCaseListEnumerator = testCasesBySource.Values.ToArray().GetEnumerator();
            }
            else
            {
                // Set the enumerator for parallel yielding of sources
                // Whenever a concurrent executor becomes free, it picks up the next source using this enumerator
                this.sourceEnumerator = testRunCriteria.Sources.GetEnumerator();
            }

            return StartTestRunPrivate(eventHandler);
        }

        public void Abort()
        {
            DoActionOnAllManagers((proxyManager) => proxyManager.Abort(), doActionsInParallel: true);
        }

        public void Cancel()
        {
            DoActionOnAllManagers((proxyManager) => proxyManager.Cancel(), doActionsInParallel: true);
        }

        #endregion

        #region IParallelProxyExecutionManager methods

        /// <summary>
        /// Handles Partial Run Complete event coming from a specific concurrent proxy exceution manager
        /// Each concurrent proxy execution manager will signal the parallel execution manager when its complete
        /// </summary>
        /// <param name="proxyExecutionManager">Concurrent Execution manager that completed the run</param>
        /// <param name="testRunCompleteArgs">RunCompleteArgs for the concurrent run</param>
        /// <param name="lastChunkArgs">LastChunk testresults for the concurrent run</param>
        /// <param name="runContextAttachments">RunAttachments for the concurrent run</param>
        /// <param name="executorUris">ExecutorURIs of the adapters involved in executing the tests</param>
        /// <returns>True if parallel run is complete</returns>
        public bool HandlePartialRunComplete(
            IProxyExecutionManager proxyExecutionManager,
            TestRunCompleteEventArgs testRunCompleteArgs,
            TestRunChangedEventArgs lastChunkArgs,
            ICollection<AttachmentSet> runContextAttachments,
            ICollection<string> executorUris)
        {
            var allRunsCompleted = false;

            // In Case of Cancel or Abort, no need to trigger run for rest of the data
            // If there are no more sources/testcases, a parallel executor is truly done with execution
            if (testRunCompleteArgs.IsAborted || testRunCompleteArgs.IsCanceled || !StartTestRunOnConcurrentManager(proxyExecutionManager))
            {
                lock (executionStatusLockObject)
                {
                    // Each concurrent Executor calls this method 
                    // So, we need to keep track of total runcomplete calls
                    runCompletedClients++;
                    allRunsCompleted = (runCompletedClients == concurrentManagerInstances.Length);
                }

                // verify that all executors are done with the execution and there are no more sources/testcases to execute
                if (allRunsCompleted)
                {
                    // Reset enumerators
                    sourceEnumerator = null;
                    testCaseListEnumerator = null;

                    // Dispose concurrent executors
                    // Do not do the cleanuptask in the current thread as we will unncessarily add to execution time
                    lastParallelRunCleanUpTask = Task.Run(() =>
                    {
                        UpdateParallelLevel(0);
                    });
                }
            }

            return allRunsCompleted;
        }

        #endregion

        #region ParallelOperationManager Methods

        protected override void DisposeInstance(IProxyExecutionManager managerInstance)
        {
            if (managerInstance != null)
            {
                try
                {
                    managerInstance.Dispose();
                }
                catch (Exception)
                {
                    // ignore any exceptions
                }
            }
        }

        #endregion

        private int StartTestRunPrivate(ITestRunEventsHandler runEventsHandler)
        {
            // Cleanup Task for cleaning up the parallel executors except for the default one
            // We do not do this in Sync so that this task does not add up to execution time
            if (lastParallelRunCleanUpTask != null)
            {
                try
                {
                    lastParallelRunCleanUpTask.Wait();
                }
                catch (Exception ex)
                {
                    // if there is an exception disposing off concurrent executors ignore it
                    if (EqtTrace.IsWarningEnabled)
                    {
                        EqtTrace.Warning("ParallelTestRunnerServiceClient: Exception while invoking an action on DiscoveryManager: {0}", ex);
                    }
                }
                lastParallelRunCleanUpTask = null;
            }

            // Reset the runcomplete data
            runCompletedClients = 0;

            // One data aggregator per parallel run
            var runDataAggregator = new ParallelRunDataAggregator();
            concurrentManagerHandlerMap = new Dictionary<IProxyExecutionManager, ITestRunEventsHandler>();

            for (int i = 0; i < concurrentManagerInstances.Length; i++)
            {
                var concurrentManager = concurrentManagerInstances[i];

                var parallelEventsHandler = new ParallelRunEventsHandler(concurrentManager, runEventsHandler,
                    this, runDataAggregator);
                concurrentManagerHandlerMap.Add(concurrentManager, parallelEventsHandler);

                Task.Run(() => StartTestRunOnConcurrentManager(concurrentManager));
            }

            return 1;
        }

        /// <summary>
        /// Triggers the execution for the next data object on the concurrent executor
        /// Each concurrent executor calls this method, once its completed working on previous data
        /// </summary>
        /// <param name="source"></param>
        /// <returns>True, if execution triggered</returns>
        private bool StartTestRunOnConcurrentManager(IProxyExecutionManager proxyExecutionManager)
        {
            TestRunCriteria testRunCriteria = null;
            if (!hasSpecificTestsRun)
            {
                string nextSource = null;
                if (FetchNextSource(sourceEnumerator, out nextSource))
                {
                    EqtTrace.Info("ProxyParallelExecutionManager: Triggering test run for next source: {0}", nextSource);

                    testRunCriteria = new TestRunCriteria(new List<string>() { nextSource },
                        actualTestRunCriteria.FrequencyOfRunStatsChangeEvent,
                        actualTestRunCriteria.KeepAlive,
                        actualTestRunCriteria.TestRunSettings,
                        actualTestRunCriteria.RunStatsChangeEventTimeout,
                        actualTestRunCriteria.TestHostLauncher);
                }
            }
            else
            {
                List<TestCase> nextSetOfTests = null;
                if (FetchNextSource(testCaseListEnumerator, out nextSetOfTests))
                {
                    EqtTrace.Info("ProxyParallelExecutionManager: Triggering test run for next source: {0}", nextSetOfTests?.FirstOrDefault()?.Source);

                    testRunCriteria = new TestRunCriteria(
                        nextSetOfTests,
                        actualTestRunCriteria.FrequencyOfRunStatsChangeEvent,
                        actualTestRunCriteria.KeepAlive,
                        actualTestRunCriteria.TestRunSettings,
                        actualTestRunCriteria.RunStatsChangeEventTimeout,
                        actualTestRunCriteria.TestHostLauncher);
                }
            }

            if (testRunCriteria != null)
            {
                proxyExecutionManager.StartTestRun(testRunCriteria, concurrentManagerHandlerMap[proxyExecutionManager]);
            }

            return (testRunCriteria != null);
        }

        /// <summary>
        /// Fetches the next data object for the concurrent executor to work on
        /// </summary>
        /// <param name="source">sourcedata to work on - sourcefile or testCaseList</param>
        /// <returns>True, if data exists. False otherwise</returns>
        private bool FetchNextSource<T>(IEnumerator enumerator, out T source)
        {
            source = default(T);
            var hasNext = false;
            lock (sourceEnumeratorLockObject)
            {
                if (enumerator.MoveNext())
                {
                    source = (T)(enumerator.Current);
                    hasNext = (source != null);
                }
            }

            return hasNext;
        }
    }
}
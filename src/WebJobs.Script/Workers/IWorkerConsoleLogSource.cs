﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Threading.Tasks.Dataflow;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    public interface IWorkerConsoleLogSource
    {
        ISourceBlock<string> LogStream { get; }

        void Log(string consoleLog);
    }
}
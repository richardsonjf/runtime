// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;

namespace System.Net.Sockets.Tests
{
    internal struct  PortAssignment : IDisposable
    {
        internal PortAssignment(int port)
        {
            Port = port;
        }

        public int Port { get; }

        public void Dispose()
        {
        }
    }

    internal static class TestPortPool
    {
        private static ConcurrentBag<int> s_usedPorts = new ConcurrentBag<int>();

        public static PortAssignment Rent()
        {
            throw new NotImplementedException();
        }

        public static void Return(PortAssignment portAssignment)
        {
            throw new NotImplementedException();
        }
    }
}

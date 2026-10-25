global using System.Buffers.Text;
global using System.Text;
global using PrefixClassName.MsTest;
global using Shouldly;

// Tests share no state (each loopback test listens on its own port), so they can run fully in parallel.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

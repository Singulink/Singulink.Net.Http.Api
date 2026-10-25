global using System.Net;
global using PrefixClassName.MsTest;
global using Shouldly;
global using Singulink.Net.Http.Api;

// Every test uses its own client and fake handler, so tests can run fully in parallel.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

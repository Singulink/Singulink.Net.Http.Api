global using System.Net;
global using Microsoft.AspNetCore.Http;
global using PrefixClassName.MsTest;
global using Shouldly;
global using Singulink.Net.Http.Api;

// Every test builds its own isolated host, so tests can run fully in parallel.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

global using System.Net;
global using System.Text.Json;
global using Microsoft.AspNetCore.Http;
global using PrefixClassName.MsTest;
global using Shouldly;
global using Singulink.Net.Http.Api;
global using Singulink.Net.Http.Api.Service.SignIn;

// Every test uses its own services and authenticators, so tests can run fully in parallel.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

global using PrefixClassName.MsTest;
global using Shouldly;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

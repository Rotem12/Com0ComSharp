# Framework sample

C# 7.3 console sample targeting Framework 4.6.2, 4.7.2, 4.8, and 4.8.1. It runs read-only discovery and compiles a simple device lifecycle example.

```powershell
dotnet build samples/Com0ComSharp.Framework -c Release
.\samples\Com0ComSharp.Framework\bin\Release\net48\Com0ComSharp.Framework.exe
```

See [Framework setup](../../docs/framework-compatibility.md). CI also builds this sample against the packaged NuGet library.

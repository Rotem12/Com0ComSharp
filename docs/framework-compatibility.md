# .NET Framework

The same NuGet package supports Framework 4.6.2–4.8.1 and .NET 8–10. API calls and constructor-based options work with C# 7.3.

Use the helpers from `Com0ComSharp.Helpers.win-x64.zip` with any supported application, including x86 Framework apps on x64 Windows. The broker and helper are self-contained.

`Com0ComSharp.Tool.net48.zip` is an optional Framework 4.8 helper. Extract all its files and also deploy `Com0ComSharp.Broker.exe` from the helpers archive.

Enable binding redirects in Framework executable projects and deploy the generated `.exe.config`:

```xml
<PropertyGroup>
  <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
</PropertyGroup>
```

See the [C# 7.3 sample](../samples/Com0ComSharp.Framework). Applications using `DriverDownload` on Framework 4.6.2 also need the TLS settings in its `App.config` and a `System.Net.Http` reference.

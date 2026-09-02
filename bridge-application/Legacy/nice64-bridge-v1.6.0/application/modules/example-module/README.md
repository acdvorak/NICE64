# NICE64 Example Module

This package is SDK reference material only.

It is not registered in `ModuleRegistry`, not selectable in the production Bridge UI, and not loaded by the current Bridge runtime.

It demonstrates the minimum shape of a future module:

- `module.json` declares identity and resource prefix.
- `content/hello.txt` owns the resource bytes.
- `ExampleModule.cs` shows how module code can expose `example://hello`.

The example intentionally does not perform networking, AI, RSS, web fetching, asset streaming, or dynamic DLL loading.

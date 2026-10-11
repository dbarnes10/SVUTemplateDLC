DLCFramework.Interfaces.dll
===========================

The compiled interface contract between an SVU DLC plugin and the host.
Your plugin assembly references this DLL and nothing else from the
framework. Do not edit, move or rename it.

It provides, among others:
  - IDLCPlugin, IDLCContext, IDLCSyncable   (plugin lifecycle and services)
  - svuSettable, svuSetterCallbackIF         (web UI parameters)
  - DLCManifest                              (manifest.json model)
  - DLCIdentity, DLCWireManifest, IDLCWireable (prefab wiring)
  - DLCLightingScope, DLCLightingRig, DLCViewController

The host project compiles the same assembly from source. When the
framework's interfaces change, a new DLL is published into this folder;
plugins built against an older DLL may need rebuilding.

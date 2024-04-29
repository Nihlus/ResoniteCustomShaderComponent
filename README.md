Custom Shader Component for Resonite
====================================

This is a plugin for the VR sandbox Resonite, enabling the use of custom shaders
in-game. Since this is a plugin, it does not allow you to use custom shaders in
sessions with players who do not also have the plugin installed.

## Building
```bash
dotnet build -c Release
```

## Publishing
```bash
dotnet publish -c Release
```

## Installing
Unpack the generated .zip file from 
./bin/publish/ResoniteCustomShaderComponent-<version>.zip into your Resonite
installation directory.

Alternatively, copy all files in the ./bin/Release/plugin/client/Libraries/
directory to the Libraries folder in your Resonite installation directory.

After using either of the two methods above, add 
`-LoadAssembly Libraries/ResoniteCustomShaderComponent.dll` to your launch 
arguments.

## Usage
Once in-game, navigate to the slot you'd like to put the shader component on.
Open the component browser and navigate to `Assets > Materials`. There, you'll
find a new component called "CustomShader".

Add the component. You will now have a component with one writable field and
two read-only fields. In ShaderURL, paste the `resdb://` link to the shader you
want to load.

Once you've done so, the `Status` field will change and display the load state.
It might take a while to load a previously-unknown shader, so be patient.  I've
seen anything between 5 seconds to several hours. Loading will continue in the
background, so you can keep doing other things while you wait. If you log out, 
loading will resume the next time the component becomes active - the shader 
itself will continue to be worked on by the Resonite servers regardless of your
logged-in status.

After the shader finishes loading, a new component will be added to the same
slot as the `CustomShader` component. This component will be a material that you
can then add to mesh renderers just like any other material, hook Flux up to its
properties, configure by hand, etc.

The material and its `CustomShader` component are to be considered "linked", so 
if you delete the `CustomShader` component, the shader material component will
also be deleted. Clearing the URL in the shader component will delete the 
generated material component.

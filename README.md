# Glitched Locale Client

## Client implementation of the [Glitched Locale Server](https://glitchedpolygons.com/store/software/glitched-locale-server) for [Godot](https://godotengine.org/) projects.

[![API](https://img.shields.io/badge/api-docs-informational.svg)](https://glitchedpolygons.github.io/UnityGlitchedLocaleClientDocs/api/GlitchedPolygons.Localization.html)

This is the official Glitched Polygons client library for usage with the [Glitched Locale Server](https://glitchedpolygons.com/store/software/glitched-locale-server).

* Currently implements standard Godot `Button` as well as `Label` nodes.
* To install, just import both scripts into your project and add a `LocalizationBucket` to your scene (with the correct credentials and config set up properly) and add a `LocalizedNode` child to buttons and labels that you want to localize: assign the `LocalizationBucket` to the inspector field as well as the localization key and various other options.

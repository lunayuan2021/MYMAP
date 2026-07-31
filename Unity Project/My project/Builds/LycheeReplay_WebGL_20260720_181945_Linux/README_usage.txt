Lychee Replay WebGL package
==========================

Open this package through an HTTP server. Do not double-click index.html and do not upload only the Build folder.

Required folders/files:
- index.html
- Build/
- StreamingAssets/
- TemplateData/

Local test:
- Linux: run ./start.sh. Default port is 9091, URL prefix /litchi_delivery_replay/; set NO_OPEN=1 to start without opening a browser.
- Windows: double-click start.bat or start_local_server.bat. Use stop.bat to stop it.
- External replay: http://host:9091/litchi_delivery_replay/?path=/data/results/game/replay/serve/replay.txt
- Map switch: http://host:9091/litchi_delivery_replay/?map=map_1 ; selector: http://host:9091/litchi_delivery_replay/?map=select
- Map + replay: http://host:9091/litchi_delivery_replay/?map=map_1&path=/data/results/game/replay/serve/replay.txt

Offline use:
- The package includes Build, StreamingAssets, UITheme, Video, and default replay.txt. No internet is required.
- http://127.0.0.1:9091/litchi_delivery_replay/ loads packaged StreamingAssets/replay.txt.
- ?path=/data/results/.../replay.txt reads a local Linux filesystem path through the bundled server.
- Only ?path=http://... requires network access.

Render quality:
- Default: follows display DPI up to DPR 2.
- 4K/UHD check: add ?quality=4k, or append &quality=4k when the URL already has ?path=.... You can also set ?dpr=2 / ?dpr=3 explicitly.
- Performance mode: add ?quality=performance or ?dpr=1.

Build scenes:
Assets/Scenes/GeneratedMaps/Map_map4.unity
Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity
Assets/Scenes/GeneratedMaps/Map_medium.unity
Assets/Scenes/GeneratedMaps/Map_map2.unity
Assets/Scenes/GeneratedMaps/Map_map3.unity
Assets/Scenes/GeneratedMaps/LycheeMapSelect.unity

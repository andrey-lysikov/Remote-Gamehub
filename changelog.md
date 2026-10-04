# Changelog

## 2.2

* Imrove support PlayStation controllers
* The mouse pointer now by Assembly tools
* HDR/SDR by shaders, Smoother gradients. The colour conversion dithers instead of leaving steps

## 2.1

* Windows Firewall rules for the streaming ports are added automatically ([Network] Firewall)
* Moonlight finds the host again: discovery is published through Windows' own mDNS responder
* The "new version is out" link on the status page is no longer cut off, and "Has new version?" is translated
* The starting card is shown again for Steam games: Steam's own window flashing up at launch no longer hides it

## 2.0

* Allow programs without a stream (for example llama-server)
* Russian interface, chosen automatically when Windows used it
* Redesign Game editor split into tabs
* Redesign Diagnostics bar (show preview and logs for running program)
* Restart host and Shut down host buttons on status page
* SQLite updated to 3.53

## 1.6

* Clients now fetch a game's cover again when it changes
* Some settings have been removed as they are no longer required

## 1.5

* If uPNP enabled, we can ban ips if it ddos.
* Now we watch if power mode changed
* Status web page and css moved from code to statui 
* Update game launcher start commands
* Reset game preferences from status page
* Update check from status page
* Bugfix

## 1.4

* Installer now can update installed program
* Recreate display if RDP session is active

## 1.3

* Fix the worker being ended by the service at every remote desktop connection
* Add vscode bundles
* Add macos/linux build scripts

## 1.2

* Add service for UAC resolve
* Fix opus multichannel warning

## 1.1

* Update HDR analyse
* Fix desktop scales
* Clean status of clentt and host

## 1.0

* Full 5.1 and 7.1 surround sound support
* Virtual mouse, if you don't have a HID device connected
* Virtual display support (you need to install the driver yourself)
* DualShock 4 controllers

## 0.7

* Bugfix audio channels
* Bugfix HDR
* Installer

## 0.5

* Initial code
* Desktop and game streaming to any Moonlight client, up to 4K
* Encoding on the graphics card: NVENC on NVIDIA and AMF on AMD (H.264, HEVC and AV1) in 4:2:0
or 4:4:4
* High dynamic range when the screen supports it
* Gamepad support, if controller bus is installed
* Games found automatically in the launchers — Steam, Xbox and Game Pass, Epic, GOG, EA,
Battle.net — and in folders of your own
* Cover art fetched automatically for the games found, from the store catalogue
* Automatic discovery, so a client finds this machine without being given an address
* Optional UPnP forwarding of the streaming ports, for playing over the internet
* The screen is put into the mode the client asked for while it streams, and back afterwards


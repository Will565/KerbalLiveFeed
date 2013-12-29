# Phase 1

## Fix storage access

* Use System.IO (instead of File.IO wrapper)
  * Enforce strict usage guidelines.
* Use named pipe instead of file manipulation for IPC.
  * [MSDN reference](http://msdn.microsoft.com/en-us/library/bb762927(v=vs.100)): http://msdn.microsoft.com/en-us/library/bb762927(v=vs.100)
  * [codeguru example](http://www.codeguru.com/csharp/csharp/cs_misc/sampleprograms/article.php/c7259/InterProcess-Communication-in-NET-Using-Named-Pipes-Part-1.htm)


## Connectivity

* Change protocol from UDP to TCP for reliability
* Consider [using compression](https://github.com/mono/mono/tree/master/mcs/class/System/System.IO.Compression)


## Screen capture methods

* Improve screenshot capture method.  (Wait for end of frame.)
  * [unity answers example](http://answers.unity3d.com/questions/22954/how-to-save-a-picture-take-screenshot-from-a-camer.html)
  * [wiki example](http://wiki.unity3d.com/index.php/ScreenCapture)
  * Indicate progress, detect failures. (maybe)


# Phase 2

## Focus Model

* Fix focus system
  * https://docs.unity3d.com/Documentation/ScriptReference/GUI.FocusWindow.html
  * Consume events when in textareas or over GUIs/buttons.

## GUI

* GUI events should be managed inside OnGUI, but GUI modified outside.


# Phase 3

## Merge Client to Plugin

* From:  Plugin -> PluginClient -> Client -> ClientServer -> Server
* To:  Plugin -> Server
* Import features of the external Client to the Plugin.
* Backport GUI from KMP.
  * Label, Field - Name
  * Label, Field - Host:Port
  * Button - Add to favourites
  * List - Favourites
    * List Item - "Host:port"
      * Button - Remove Favourite
  * Button Toggle - Connect/Disconnect
  * Button Toggle - "Auto-Reconnect On/Off"
* Create method for determining current working directory (Using System.IO)
  * Useful for finding saves directory for craft sharing.
* Store Client options in the PluginData directory
* Retain Client until Phase 2 confirmed working (alternative, chat-only)

## WCF standard

* Replace communication model with WCF.
  * Create KLF message protocol diagram
  * Apply WCF structure, [reference](http://tech.pro/tutorial/855/wcf-tutorial-basic-interprocess-communication)

## Authentication

* Backport token system from KMP.
* Client reads configuration file for username and token.
  * If no token, generate randomly and save to config.
  * All messages sent from Clients contain this username and token.
* Server recieves messages
  * Compare username and token to a local authentication list.
  * If Server has no entry for this username, add to list with token.
  * If username and token matches local list, relay message to all Clients.
  * If token does not match, relay message with warn prefix. (Unauthenticated)


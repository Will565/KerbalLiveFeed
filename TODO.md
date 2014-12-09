Ideas scratch pad

* Consider changing protocol from UDP to entirely TCP for reliability
* Consider [using compression](https://github.com/mono/mono/tree/master/mcs/class/System/System.IO.Compression)
* Improve screenshot capture method.  e.g. Wait for end of frame.
  * [unity answers example](http://answers.unity3d.com/questions/22954/how-to-save-a-picture-take-screenshot-from-a-camer.html)
  * [wiki example](http://wiki.unity3d.com/index.php/ScreenCapture)
* Fix focus system
  * https://docs.unity3d.com/Documentation/ScriptReference/GUI.FocusWindow.html
  * Consume events when in textareas or over GUIs/buttons.
* GUI events should be managed inside OnGUI, but GUI modified outside.
* Add onclick event queue for shared links to open browser e.g.
      button.OnClick += (e) => Application.OpenURL(magicHypertextThingie.matchButton());
* From:  Plugin -> PluginClient -> Client -> ClientServer -> Server
* To:  Plugin -> Server
* Import features of the external Client to the Plugin.
* Backport GUI from KMP, send command messages to Console App.
  * Label, Field - Name
  * Label, Field - Host:Port
  * Button - Add to favourites
  * List - Favourites
    * List Item - "Host:port"
      * Button - Remove Favourite
  * Button Toggle - Connect/Disconnect
  * Button Toggle - "Auto-Reconnect On/Off"
* Create method for determining current working directory (Using System.IO)
  * Useful for debugging bad installs and finding saves directory for craft sharing.
  * Perhaps store Client options in the PluginData directory
* Replace communication model with serialized WCF.
  * Create KLF message protocol diagram
  * Apply WCF structure, [reference](http://tech.pro/tutorial/855/wcf-tutorial-basic-interprocess-communication)
* Backport token system from KMP.
* Client reads configuration file for username and token.
  * If no token, generate randomly and save to config.
  * All messages sent from Clients contain this username and token.
* Server recieves messages
  * Compare username and token to a local authentication list.
  * If Server has no entry for this username, add to list with token.
  * If username and token matches local list, relay message to all Clients.
  * If token does not match, relay message with warn prefix. (Unauthenticated)


# Dutch Skies - A Mixed Reality view of air traffic over The Netherlands



Dutch Skies is Copyright (C) 2021 Paul Melis, SURF (paul.melis@surf.nl)


## Requirements

In order to build the application you will need:

* Visual Studio, set up for Windows UWP development
* [StereoKit](https://stereokit.net/) version XXX installed

In order to test Dutch Skies in mixed reality you will need a Microsoft HoloLens,
preferably version 2.


## Disclaimers

Dutch Skies has not been tested outside of Amsterdam, The Netherlands. So with
different geographical locations some bugs will probably surface :) I'm especially 
curious if it works without issue on the Southern Hemisphere when using a custom
map or sky-mode location (see below).

This application has only been tested with a Microsoft HoloLens 2. It might
also work on other devices. Running it in StereoKit desktop mode also works, but that 
does not really provide the full experience.


## How to use a custom map or sky-mode location

By default, only Amsterdam and Schiphol Airport are included as maps. The 
Dutch sky is quite busy with planes, including ones arriving/departing from overseas,
so this should provide some interesting views out-of-the-box. But you might want
to view a different area yourself.

XXX



## License

All files, except those listed under the Attributions section below, are licensed
under a Creative Commons [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)
license, which is summarized below. See LICENSE.txt for the full licensing terms. 

* You are free to:

* Share - copy and redistribute the material in any medium or format

* Adapt - remix, transform, and build upon the material 

Under the following terms:

* Attribution - You must give appropriate credit, provide a link to the license, and 
  indicate if changes were made. You may do so in any reasonable manner, but not in 
  any way that suggests the licensor endorses you or your use. 

* NonCommercial - You may not use the material for commercial purposes. 

* ShareAlike - If you remix, transform, or build upon the material, you must 
  distribute your contributions under the same license as the original. 
  
* No additional restrictions - You may not apply legal terms or technological measures that legally restrict others from doing anything the license permits. 


### OpenSky Network real-time data

Note that this application makes use of the [OpenSky Network](https://opensky-network.org/)
as a real-time data provider. As such, when using this application, you are bound to
its [General Terms of Use & Data License Agreement](https://opensky-network.org/about/terms-of-use).


## FAQ

* In Sky Mode the virtual planes are not aligned very well with the real-world planes!

  Yes, this is expected and almost unavoidable, although the virtual overlay
  can be tweak to be fairly close to the real planes. It certainly should be accurate enough
  to track and identify the real planes without too much trouble.
  
  There are (at least) three reasons for the mismatch:
  
  1. The real-time plane tracking data provided by the OpenSky Network is pretty
     accurate when it comes to a plane's position, but the timestamp attached
     to that data point only has a granularity of one second (the `time_position`
     field in the [REST API](https://openskynetwork.github.io/opensky-api/rest.html)).
     In that one second a fast-flying plane will move quite a bit across the sky. 
     So even though the position is given with precision, the exact moment of *when* 
     the plane was at that position is not known with much precision. 
     
  2. As the data from the OpenSky Network only provides a plane's location at some
     moment in the past, a prediction needs to be made where the plane is *right now*, in order
     to overlay the virtual plane. This prediction isn't currently very good, especially
     for planes that are changing course and speed a lot (e.g. when making a turn to line
     up with the runway). The prediction can probably be improved, e.g. using
     Kalman filtering.
     
  3. Alignment of the Mixed Reality world with the physical world is imprecise. The HoloLens 2
     does not have location sensors, although Wifi-based location tracking might be
     of some use. The manual specification of the current location (using a QR code
     specifying lat/lon/altitude) can be pretty accurate, and any small deviations from
     the ground truth there will not lead to much misalignment. 
     
     But setting the *orientation* of the virtual world with any precision is a bit 
     tricky, and any rotation away from true North will be quite noticeable, as the 
     virtual planes will be either behind or in front of their real planes.    

* How about including terrain height in the map?

  For the case of The Netherlands this doesn't make much sense. The highest
  "mountain" in The Netherlands is 321m high (the Vaalserberg). The lowest point
  is around 6.74m *below* sea level. The default map is around 315km wide, shown
  at 1.5m physical size in the MR world. That translates to 210m of height
  per *millimeter* of map. So the height difference for The Netherlands would amount
  to less than 2 millimeters in MR, not really worth it.
  
  For a different area in the world it might make more sense to includ height, though.
  
* Why not use a (3D) terrain service, such as Bing Maps? 

  Indeed, there is the Stereo Kit [Bing Maps sample](https://github.com/maluoi/StereoKit-BingMaps),
  although I did not try that, nor looked at the code. The reason is that it
  requires a Bing Maps API key, plus it would base this code on non-free (and commercial)
  software. The same would hold for other similar services.
  
  The current setup, based on pre-generated OpenStreetMap maps, is simple and free to 
  use and customize. It does not need registration, nor an API key. Adding your
  own maps is fairly easy, see XXX.

* Why not simply hard-code OpenStreetMap tile downloading in the code?

  This is against the OSM [Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/),
  or at least, it's strongly discouraged.
  

## Attributions

* SimpleJSON.cs is from the [SimpleJSON](https://github.com/Bunny83/SimpleJSON) repository.
  See SimpleJSON-LICENSE.txt for the original license.
  
* Airplane 3D model based on a model retrieved from Google Poly (which it was still active) 
  under the name "Airplane", originally licensed under a [CC-BY 3.0](https://creativecommons.org/licenses/by/3.0/) 
  license. The included model is an edited version of the retrieved model. 

* Compass rose adapted from https://commons.wikimedia.org/wiki/File:Windrose.svg,
  original (Windrose.original.svg) and redistributed file (Windrose.svg) licensed 
  under [Creative Commons Attribution-Share Alike 3.0 Unported]
  (https://creativecommons.org/licenses/by-sa/3.0/deed.en).
  

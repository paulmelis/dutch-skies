# Dutch SKies - A Mixed Reality view of air traffic over The Netherlands (and elsewhere)


XXX Submitted to the [Mixed Reality Challenge: StereoKit (C# and OpenXR)](https://mixed-reality-stereokit.devpost.com/)


Dutch SKies is Copyright (C) 2021 Paul Melis, SURF (paul.melis@surf.nl)

## Requirements

In order to build the application you will need:

* Visual Studio, set up for Windows UWP development, with these two packages installed (through NuGet):
  * [StereoKit](https://stereokit.net/) (developed and tested with 0.3.4)
  * Microsoft.MixedReality.QR
* Python 3, in case you want to run some of the scripts under `Scripts`. Modules used by
  the scripts are
  * [requests](https://pypi.org/project/requests/)
  * [qrcode](https://pypi.org/project/qrcode/) 
  * [PIL(LOW)](https://pypi.org/project/Pillow/)

In order to test Dutch SKies in mixed reality you will need a Microsoft HoloLens 2.

## Disclaimers

Dutch SKies has not been tested outside of Amsterdam, The Netherlands. So with
different geographical locations some bugs will probably surface :) I'm especially 
curious if it works without issues on the Southern Hemisphere when using a custom
map or sky-mode location (see below).

This application has only been tested with a Microsoft HoloLens 2. It might
also work on other devices. Running it in StereoKit desktop mode also works, of course,
but that does not really provide the full experience.

## OpenSky Network real-time data

Note that this application makes use of the [OpenSky Network](https://opensky-network.org/)
as a real-time data provider. As such, when using this application, you are bound to
its [General Terms of Use & Data License Agreement](https://opensky-network.org/about/terms-of-use).

## How to use a custom map or sky-mode location

By default, only Amsterdam and Schiphol Airport are included as (hard-coded)maps. The 
Dutch sky is relatively busy with planes, including ones crossing from/to overseas,
others landing at airports nearby the country, etc. So this should provide some interesting 
views out-of-the-box for a country-scale airspace. But you might want to view a different area yourself,
of course.

You will need 3 things to use a custom map/sky view:

1. A JSON file describing the map extent, map image and observer location data. You can also
   include a number of landmarks to fine-tune the alignment of the MR world with the real world.

2. An online place to host the JSON file, and any map images, from step 1. This can be anywhere (a Google Drive,
   your own server, etc), as long as you can provide a URL to the JSON file that is accessible
   by the HoloLens on on which you plan to run Dutch SKies.

3. A QR code that contains the URL from step 2. You can use `Scripts/text2qrcode.py` to generate
   a PNG image holding the QR code for a given URL. You do not have to print this QR code image, but
   it does need to be visible in sufficient size for the HoloLens to be able to scan it successfully.
   The Microsoft docs suggest at least 5x5 cm, but that seems a bit small. Showing the image full-screen
   on your monitor will allow the HoloLens to scan the QR code, even when it's some distance away.

   Note that StereoKit can take advantage of the QR code's orientation, but this currently isn't used 
   in Dutch SKies.

   By using the QR code only for providing the URL to the actual configuration allows you to update
   that configuration without having to update the QR code.

### Creating and configuring a map

XXX map image adv: can add annotations, faster to retrieve

The simplest way to generate a map and relevant meta-data is using `Script/make_osm_map.py`. 

For example, let's create a map for the San Francisco Bay area, where there are lots of airports.
On [https://www. openstreetmap.org](https://www. openstreetmap.org) we can use the Export function
and then choose `Manually select a different area` just below the extent numbers to select the extent
of our map:

![](./Scripts/osm-export.jpg)

We then use the extent listed (and zoom level based on the one from the URL) as arguments to `make_osm_map.py`.
The zoom level determines the resolution of the output image. At zoom level 11 the resulting map covers 6x6 tiles and is 1536x1536 pixels, at zoom level 12 it covers 12x11 tiles and is 3,072x2816 pixels. Here, we use zoom level 12:

```
$ ./make_osm_map.py 37.2008 37.9193 -122.6212 -121.6791 12 sanfrancisco
tile range: i = 652-663, j = 1581-1591
map range:  37.160316546736766 -122.6953125 -> 37.92686760148134 -121.640625
center:  37.54359207410906 -122.16796875
Map size at center = 93.070 x 85.332 km
Retrieving 12 x 11 tiles
(652,1581) (653,1581) (654,1581) (655,1581) (656,1581) (657,1581) (658,1581) (659,1581) (660,1581) (661,1581) (662,1581) (663,1581) (652,1582) (653,1582) (654,1582) (655,1582) (656,1582) (657,1582) (658,1582) (659,1582) (660,1582) (661,1582) (662,1582) (663,1582) (652,1583) (653,1583) (654,1583) (655,1583) (656,1583) (657,1583) (658,1583) (659,1583) (660,1583) (661,1583) (662,1583) (663,1583) (652,1584) (653,1584) (654,1584) (655,1584) (656,1584) (657,1584) (658,1584) (659,1584) (660,1584) (661,1584) (662,1584) (663,1584) (652,1585) (653,1585) (654,1585) (655,1585) (656,1585) (657,1585) (658,1585) (659,1585) (660,1585) (661,1585) (662,1585) (663,1585) (652,1586) (653,1586) (654,1586) (655,1586) (656,1586) (657,1586) (658,1586) (659,1586) (660,1586) (661,1586) (662,1586) (663,1586) (652,1587) (653,1587) (654,1587) (655,1587) (656,1587) (657,1587) (658,1587) (659,1587) (660,1587) (661,1587) (662,1587) (663,1587) (652,1588) (653,1588) (654,1588) (655,1588) (656,1588) (657,1588) (658,1588) (659,1588) (660,1588) (661,1588) (662,1588) (663,1588) (652,1589) (653,1589) (654,1589) (655,1589) (656,1589) (657,1589) (658,1589) (659,1589) (660,1589) (661,1589) (662,1589) (663,1589) (652,1590) (653,1590) (654,1590) (655,1590) (656,1590) (657,1590) (658,1590) (659,1590) (660,1590) (661,1590) (662,1590) (663,1590) (652,1591) (653,1591) (654,1591) (655,1591) (656,1591) (657,1591) (658,1591) (659,1591) (660,1591) (661,1591) (662,1591) (663,1591) 
Writing output image sanfrancisco.png ... done

$ ls -l sanfrancisco.png 
-rw-r--r-- 1 melis users 6815348 Dec  2 22:48 sanfrancisco.png

$ identify sanfrancisco.png
sanfrancisco.png PNG 3072x2816 3072x2816+0+0 8-bit sRGB 6.49962MiB 0.000u 0:00.000

$ cat sanfrancisco.map.json 
{
    "image_source": {
        "type": "url",
        "url": "sanfrancisco.png"
    },
    "lat_range": [
        37.160316546736766,
        37.92686760148134
    ],
    "lon_range": [
        -122.6953125,
        -121.640625
    ],
    "name": "sanfrancisco",
    "zoom": 12
}
```

Note that the actual lat/lon range of the map image is larger than what we specified. This is expected, as only full image tiles are used to cover
the input lat/lon, without clipping the tiles.

The JSON file produced next to the image can be used to set up the necessary configuration JSON file:

```
{
    "query": {
        "lat_range": [
            37.160316546736766,
            37.92686760148134
        ],
        "lon_range": [
            -122.6953125,
            -121.640625
        ]
    },

    "maps": [
        {
            "image_source": {
                "type": "url",
                "url": "sanfrancisco.png"
            },
            "lat_range": [
                37.160316546736766,
                37.92686760148134
            ],
            "lon_range": [
                -122.6953125,
                -121.640625
            ],
            "name": "sanfrancisco",
            "zoom": 12
        }
    ]
}
```

We upload this JSON file and `sanfrancisco.png` to a location where the Dutch SKies app can retrieve it. We then generate a QR code for this URL,
display it on a PC screen and use the `Scan QR code` button in the UI to initiate scanning. As soon as a QR code is recognized the scan function is disabled
(including button release sound) and the configuration will get loaded. In the process the map image is retrieved and applied. 

You can check the Log window to check for errors is something goes wring in this workflow.

XXX HL screenshot

XXX Is it? XXX If you don't set an observation location the center of the map lat/lon extent is used by default (at altitude 0m).

#### Custom maps

The map image needs to adhere to the EPSG:3857 projection (also known as [Web Mercator](https://en.wikipedia.org/wiki/Web_Mercator_projection)).
This is the projection used by OpenStreetMap, Google Maps and many others to match [WGS84](https://en.wikipedia.org/wiki/World_Geodetic_System#A_new_World_Geodetic_System:_WGS_84)/EPSG:4326 (basically GPS coordinates) with Web Mercator. 

It is crucial that you that the WGS84 latitude/longitude map extent matches the given map image.


### Setting an observer location

Single observer location and altitude


XXX

## Bugs and known issues

* The JSON parsing isn't very robustly currently, either to syntax errors in the JSON data itself,
  nor to missing fields.

* The map surface always represents an altitude of 0m, which will look weird for planes landing at airports
  at much higher altitudes. Having a real 3D map with terrain height would fix that.

* When loading a configuration that uses a large map image the rendering can briefly be interrupted
  (with even the view going fully black), while the map is processed and uploaded to the GPU.

## FAQ

* **Can this be built for VR, e.g. Oculus Quest?**

  In principle, the code is based mostly on StereoKit, which also supports VR. But the current
  project file and solution are tied to UWP as platform. Also, the QR code scanning will currently
  not work on a Quest.

* **The planes seem to be jump a bit every few seconds**

  The updated plane location data from the OpenSky Network is only retrieved every few seconds. 
  The movement of the virtual planes is currently extrapolated based on the last received data, 
  so might be a little bit off until the next set of data points is retrieved.

  An alternative could be to retrieve the last *two* known data points for each plane and then interpolate
  the plane path between those, but that would mean plane locations would lag by about 5-10 seconds,
  which would then not match very well with the actual physical location of the planes.
  
  Also see the next question.

* **The sky-mode virtual planes are not aligned very well with real-world planes!**

  Yes, this is expected and almost unavoidable, although the virtual overlay
  can be tweaked to be fairly close to the real planes. It certainly should be accurate enough
  to track and identify real planes without too much ambiguity.
  
  There are (at least) three reasons for the mismatch:
  
  1. The real-time plane tracking data provided by the OpenSky Network is pretty
     accurate when it comes to a plane's position, but the timestamp attached
     to that data point only has a granularity of one second (the `time_position`
     field in the [REST API](https://openskynetwork.github.io/opensky-api/rest.html)).
     In that one second a fast-flying plane will move quite a bit across the sky. 
     So even though the position is given with precision, the exact moment of *when* 
     the plane was at that position is not known accurately enough. 
     
  2. As the data from the OpenSky Network only provides a plane's location at some
     moment in the past, a prediction needs to be made where the plane is *right now*, in order
     to overlay the virtual plane. This prediction isn't currently very good, especially
     for planes that are changing course and speed a lot (e.g. when making a turn to line
     up with the runway). The prediction can probably be improved, e.g. using
     Kalman filtering.
     
  3. Alignment of the Mixed Reality world with the physical world is imprecise. The HoloLens 2
     does not have location sensors, although Wifi-based location tracking might be
     of some use. The manual specification of the current location (using a QR code
     specifying lat/lon/altitude) can be pretty accurate, and any small deviations 
     from the ground truth in position will not lead to much misalignment. 
     
     But setting the *orientation* of the virtual world with any precision is a bit 
     tricky, and any rotation away from true North will be quite noticeable, as the 
     virtual planes will be either behind or in front of their real planes. Hence, the
     option to do orientation (and height) trimming through the UI. 

* **How about including terrain height in the map?**

  For the case of The Netherlands this doesn't have much value ;-). The highest
  "mountain" in The Netherlands is 321m high (the Vaalserberg). The lowest point
  is around 6.74m *below* sea level. The default map is around 315km wide, shown
  at 1.5m physical size in the MR world. That translates to 210m of height
  per *millimeter* of physical (MR) map size. So the height difference for The 
  Netherlands would amount to less than 2 millimeters in MR, not really worth it.
  
  For a different area in the world, or when using a much smaller map of mountain
  terrain, it might make more sense to include height, though.

* **How about showing the Earth's curvature in the map?**

  XXX
  
* **Why not use a (3D) terrain service, such as Bing Maps?**

  Indeed, there is the Stereo Kit [Bing Maps sample](https://github.com/maluoi/StereoKit-BingMaps),
  although I did not try it, nor looked at the code. The reason is that it
  requires a Bing Maps API key, plus it would base this code on non-free (and commercial)
  software. The same would hold for other similar services.
  
  The current setup, based on pre-generated OpenStreetMap maps, is simple and free to 
  use and customize. It does not need registration, nor an API key. Adding your
  own maps is fairly easy, see XXX.

* **Why not simply hard-code OpenStreetMap tile downloading in the code? Why the indirection
  with the JSON files?**

  Hardcoding the URLs of OSM tile servers in the code is against the OSM [Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/),
  or at least, it's strongly discouraged. And using the JSON file to specify tile
  servers allows future extensions to use other tile schemes and servers, or to 
  use a [local OSM cache](https://github.com/paulmelis/osmcache).
  

## License

All files, except those listed under the Attributions subsection, are licensed
under a Creative Commons [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)
license, which is summarized below. See LICENSE.txt for the full licensing terms. 

You are free to:

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

### Attribution

* The map files under `Assets\Maps` are generated from [OpenStreetMap](www.openstreetmap.org) tiles,
  using the `Scripts/make_osm_map.py` script. The tiles, and the derived map images, have credit "(C) OpenStreetMap contributors"
  and are distributed under the [Open Data Commons Open Database License (ODbL)](https://opendatacommons.org/licenses/odbl/).
  See [here](https://www.openstreetmap.org/copyright) for more info.

* SimpleJSON.cs is from the [SimpleJSON](https://github.com/Bunny83/SimpleJSON) repository.
  See SimpleJSON-LICENSE.txt for the original license.
  
* The airplane 3D model is based on a model file retrieved from Google Poly (while it was still active) 
  under the name "Airplane", originally licensed under a [CC-BY 3.0](https://creativecommons.org/licenses/by/3.0/) 
  license. The included model is an edited version of the retrieved model. 

* Compass rose adapted from https://commons.wikimedia.org/wiki/File:Windrose.svg,
  original (Windrose.original.svg) and redistributed file (Windrose.svg) licensed 
  under [Creative Commons Attribution-Share Alike 3.0 Unported](https://creativecommons.org/licenses/by-sa/3.0/deed.en).
  
* The logo (`Assets/Logo.svg`) uses `Assets/Airplane_silhouette.svg` (from https://en.m.wikipedia.org/wiki/File:Airplane_silhouette.svg, 
  which is placed in the public domain). It also uses `Assets/Blank_map_of_the_Netherlands.svg` (from https://nl.wikipedia.org/wiki/Bestand:Blank_map_of_the_Netherlands.svg, which is shared under [Attribution-ShareAlike 3.0 Unported (CC BY-SA 3.0)](https://creativecommons.org/licenses/by-sa/3.0/deed)).
   
* Some files are from the StereoKit sources (`Assets/floor.hlsl`).
  

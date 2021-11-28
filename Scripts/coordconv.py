#   Converting lat, lon (epsg:4326) into EPSG:3857
# Ref: https://stackoverflow.com/questions/37523872/converting-coordinates-from-epsg-3857-to-4326-dotspatial/40403522#40403522
import math

def ConvertCoordinate(lon, lat):
    lonInEPSG4326 = lon
    latInEPSG4326 = lat

    lonInEPSG3857 = (lonInEPSG4326 * 20037508.34 / 180)
    latInEPSG3857 = (math.log(math.tan((90 + latInEPSG4326) * math.pi / 360)) / (math.pi / 180)) * (20037508.34 / 180)

    return lonInEPSG3857, latInEPSG3857


print(5, 80, ConvertCoordinate(5, 80))
print(5, 52, ConvertCoordinate(5, 52))
print(5, 2, ConvertCoordinate(5, 2))
print(5, -2, ConvertCoordinate(5, -2))
print(5, -52, ConvertCoordinate(5, -52))
print(5, -80, ConvertCoordinate(5, -80))

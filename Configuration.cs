using System.Collections.Generic;
using Windows.Storage;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    public class Configuration
    {
        /*
         * type: "maps", "landmarks", "observation_point"
         * 
         * "maps"
         *      "Netherlands" -> "{....}"
         *      ...
         *       
         * "landmarks"
         *      "Park" -> "{...}"
         *      ...
         *    
         * "observation_point"
         *      "Home" -> "{...}"     
         * 
         */

        public static void StoreConfiguration(string type, string id, JSONNode data)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

            string sdata = data.ToString();
            ApplicationDataContainer type_container;

            if (localSettings.Containers.ContainsKey(type))
                type_container = localSettings.Containers[type];
            else
                type_container = localSettings.CreateContainer(type, ApplicationDataCreateDisposition.Always);

            type_container.Values[id] = sdata;
        }

        public static void ListConfigurations(string type, ref List<string> items)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Containers.ContainsKey(type))
                return;

            ApplicationDataContainer type_container = localSettings.Containers[type];

            items.Clear();
            foreach (string id in type_container.Values.Keys)
                items.Add(id);
            items.Sort();
        }

        public static bool LoadConfiguration(string type, string id, out JSONNode node)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Containers.ContainsKey(type))
            {
                Log.Err($"No configurations of type '{type}' stored (id '{id}')");
                node = null;
                return false;
            }

            ApplicationDataContainer type_container = localSettings.Containers[type];

            if (!type_container.Values.ContainsKey(id))
            {
                Log.Err($"No configuration '{id}' found (type '{type}')");
                node = null;
                return false;
            }

            string sdata = type_container.Values[id] as string;
            node = JSONNode.Parse(sdata);

            return true;
        }

        public static void DeleteConfigurationsOfType(string type)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            localSettings.DeleteContainer(type);
        }

        public static void DeleteConfiguration(string type, string id)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Containers.ContainsKey(type))
            {
                Log.Err($"No configurations of type '{type}' stored (id '{id}')");
                return;
            }

            ApplicationDataContainer type_container = localSettings.Containers[type];
            type_container.DeleteContainer(id);
        }
    }
}
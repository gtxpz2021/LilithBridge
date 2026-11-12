using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;
internal static partial class ConfigStore
{
    internal static BridgeConfig SetDeepSeekTaskModel(string task, string model)
    {
        if (task is not ("memory" or "reflection")) throw new ArgumentException("Unknown DeepSeek task");
        DeepSeekModels.ValidateId(model);
        lock (LoadLock)
        {
            string root=DataRoot(), path=Path.Combine(root,"config.json");
            string? before=File.Exists(path)?File.ReadAllText(path):null;
            var doc=before==null?new JsonObject():JsonNode.Parse(before) as JsonObject
                ??throw new InvalidDataException("Config root must be an object");
            if(doc["llm_provider"]?.GetValue<string>()=="openai")throw new InvalidOperationException("Provider changed; choose again");
            if(doc["model_tasks"]==null)doc["model_tasks"]=new JsonObject();
            var tasks=doc["model_tasks"] as JsonObject??throw new InvalidDataException("Invalid task settings");
            if(tasks[task]==null)tasks[task]=new JsonObject();
            var owner=tasks[task] as JsonObject??throw new InvalidDataException("Invalid task settings");
            owner["model"]=model; // Existing model_tasks owner overrides legacy provider aux/reflection_model.
            Directory.CreateDirectory(root);
            string pending=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllText(pending,doc.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
                if(before!=null){if(File.ReadAllText(path)!=before)throw new IOException("Config changed; retry selection");File.Replace(pending,path,null);}
                else File.Move(pending,path);
                return Publish(ReadUncached());
            }
            finally{if(File.Exists(pending))File.Delete(pending);}
        }
    }
}

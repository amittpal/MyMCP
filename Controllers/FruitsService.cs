using ModelContextProtocol.Server;
using System.ComponentModel;

namespace MyMCP.Controllers
{
    [McpServerToolType]
    public class FruitsService
    {
        [McpServerTool(Name = "GetFruits"), Description("Gets list of fruits")]
        public string[] GetFruits()
        {
            return ["Mango", "Banana"];
        }
    }
}

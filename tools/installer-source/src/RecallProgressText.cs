using System;
namespace LilithSetup
{
    public static class RecallProgressText
    {
        public static string Parse(string value)
        {
            if (!value.StartsWith("LBPROGRESS ", StringComparison.Ordinal)) return null;
            string text = value.Substring(11);
            if (text.StartsWith("WHEEL_HASH ", StringComparison.Ordinal)) return "正在校验依赖文件 " + text.Substring(11);
            if (text.StartsWith("MODEL_HASH ", StringComparison.Ordinal)) return "正在校验模型文件 " + text.Substring(11);
            switch (text)
            {
                case "ENV_CREATE": return "正在创建隔离Python环境和本地pip";
                case "PIP_INSTALL": return "正在离线展开并安装锁定依赖（此步骤不下载）";
                case "PACKAGE_CHECK": return "正在核对已安装依赖版本与一致性";
                case "RUNTIME_IMPORTS": return "正在导入Torch等运行库并核对CPU/隔离协议";
                case "ENV_CHECK": return "正在检查隔离Python环境";
                case "WORKER_CHECK": return "正在启动本地召回验证进程";
                case "MODEL_LOAD": return "正在核验模型、导入运行库并验证临时合成检索（离线）";
                case "CHECK_RESULT": return "正在核对检索结果和向量文件";
                default: return null;
            }
        }
    }
}

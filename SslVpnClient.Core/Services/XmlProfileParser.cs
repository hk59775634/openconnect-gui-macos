using System.Xml;
using Microsoft.Extensions.Logging;
using SslVpnClient.Models;

namespace SslVpnClient.Services;

public class XmlProfileParser
{
    private readonly ILogger<XmlProfileParser> _logger;

    public XmlProfileParser(ILogger<XmlProfileParser> logger)
    {
        _logger = logger;
    }

    public List<GatewayNode> ParseGatewayNodes(string xmlContent)
    {
        var nodes = new List<GatewayNode>();

        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            throw new ProfileLoadException("profile.xml 内容为空。");
        }

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);

            var serverListNodes = doc.GetElementsByTagName("ServerList");
            foreach (XmlNode serverList in serverListNodes)
            {
                foreach (XmlNode child in serverList.ChildNodes)
                {
                    if (child.NodeType != XmlNodeType.Element ||
                        !string.Equals(child.LocalName, "HostEntry", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var node = ParseHostEntry(child);
                    if (node != null && !string.IsNullOrWhiteSpace(node.Address))
                    {
                        nodes.Add(node);
                    }
                }
            }

            var backupListNodes = doc.GetElementsByTagName("BackupServerList");
            foreach (XmlNode backupList in backupListNodes)
            {
                var addressNode = backupList.SelectSingleNode("*[local-name()='HostAddress']");
                if (addressNode != null && !string.IsNullOrWhiteSpace(addressNode.InnerText))
                {
                    nodes.Add(new GatewayNode
                    {
                        Name = "备用服务器",
                        Address = addressNode.InnerText.Trim()
                    });
                }
            }

            _logger.LogDebug("解析完成，共 {Count} 个节点", nodes.Count);
        }
        catch (XmlException ex)
        {
            _logger.LogError(ex, "XML 解析失败");
            throw new ProfileLoadException("profile.xml 格式无效，无法解析节点列表。");
        }

        return nodes;
    }

    private static GatewayNode? ParseHostEntry(XmlNode hostEntry)
    {
        string? name = null;
        string? address = null;
        string? userGroup = null;

        foreach (XmlNode child in hostEntry.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var text = child.InnerText?.Trim();
            switch (child.LocalName)
            {
                case "HostName":
                    name = text;
                    break;
                case "HostAddress":
                    address = text;
                    break;
                case "UserGroup":
                    userGroup = text;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return new GatewayNode
        {
            Name = string.IsNullOrWhiteSpace(name) ? address : name,
            Address = address,
            UserGroup = userGroup
        };
    }
}

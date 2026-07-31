using System;
using System.Collections.Generic;

[Serializable]
public class LycheeUiMapConfig
{
    public string schemaVersion;
    public string designVersion;
    public string mapId;
    public string mapName;
    public LycheeGridInfo grid;
    public LycheeMapLayer map;
    public LycheeMapLayers layers;
    public List<LycheeNodeInfo> nodes;
    public List<LycheeEdgeInfo> edges;
    public LycheeGameplayConfig gameplay;
    public List<LycheeRoutePath> routePaths;
}

[Serializable]
public class LycheeGridInfo
{
    public int width;
    public int height;
    public float tileSize = 1f;
    public string origin;
    public string xAxis;
    public string yAxis;
}

[Serializable]
public class LycheeMapLayer
{
    public string encoding;
    public string data;
}

[Serializable]
public class LycheeMapLayers
{
    public LycheeMapLayer terrainData;
    public LycheeMapLayer objectData;
    public LycheeMapLayer weatherRegionData;
}

[Serializable]
public class LycheeNodeInfo
{
    public string nodeId;
    public int code;
    public string name;
    public string type;
    public int x;
    public int y;
    public string icon;
}

[Serializable]
public class LycheeEdgeInfo
{
    public string edgeId;
    public string fromNodeId;
    public string toNodeId;
    public string routeType;
    public float distance;
    public bool bidirectional = true;
    public string pathId;
}

[Serializable]
public class LycheeRoutePath
{
    public string pathId;
    public string edgeId;
    public List<LycheeGridPoint> points;
}

[Serializable]
public class LycheeGameplayConfig
{
    public LycheeGameplayRoles roles;
    public List<LycheeGameplayResource> resources;
    public List<LycheeGameplayProcessNode> processNodes;
    public List<string> obstacleCandidateNodeIds;
}

[Serializable]
public class LycheeGameplayRoles
{
    public string startNodeId;
    public List<string> terminalNodeIds;
    public string gateNodeId;
    public List<string> safeZoneNodeIds;
    public string reverifyNodeId;
    public List<string> rushExcludedNodeIds;
}

[Serializable]
public class LycheeGameplayResource
{
    public string nodeId;
    public string resourceType;
    public int count;
    public int claimRound;
}

[Serializable]
public class LycheeGameplayProcessNode
{
    public string nodeId;
    public string processType;
    public int processRound;
    public bool canWindow;
}

[Serializable]
public class LycheeGridPoint
{
    public float x;
    public float y;
}

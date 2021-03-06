using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using DFHack;
using RemoteFortressReader;
using UnityEngine.UI;
using System.IO;
using UnityExtension;

// The class responsible for talking to DF and meshing the data it gets.
// Relevant vocabulary: A "map tile" is an individual square on the map.
// A "map block" is a 16x16x1 area of map tiles stored by DF; think minecraft "chunks".

public class GameMap : MonoBehaviour
{
    // Things to be set from the Unity Editor.
    public Material basicTerrainMaterial;   // Can be any terrain you want.
    public Material stencilTerrainMaterial; // Foliage & other stenciled materials.
    public Material waterMaterial;
    public Material magmaMaterial;
    public Material invisibleMaterial;
    public Material invisibleStencilMaterial;
    public Light magmaGlowPrefab;
    public Text genStatus;
    public Text cursorProperties;

    public bool overheadShadows = true;

    public int cursX = -30000;
    public int cursY = -30000;
    public int cursZ = -30000;


    // Parameters managing the currently visible area of the map.
    // Tracking:
    int PosXBlock
    {
        get
        {
            return posXTile / 16;
        }
    }
    int PosYBlock
    {
        get
        {
            return posYTile / 16;
        }
    }
    public int posXTile = 0;
    public int posYTile = 0;
    public int posZ = 0;
    public int PosZ
    { // Public accessor; used from MapSelection
        get
        {
            return posZ;
        }
    }
    public int PosXTile
    {
        get
        {
            return posXTile;
        }
    }
    public int PosYTile
    {
        get
        {
            return posYTile;
        }
    }

    // Preferences:
    public int rangeX = 4;
    public int rangeY = 4;
    public int rangeZup = 0;
    public int rangeZdown = 5;
    public int blocksToProcess = 1;
    public int cameraViewDist = 25;
    public int meshingThreads = 0;

    // Stored view information
    RemoteFortressReader.ViewInfo view;
    bool posZDirty = true; // Set in GetViewInfo if z changes, and reset in HideMeshes after meshes hidden.

    BlockMesher mesher;

    // The actual unity meshes used to draw things on screen.
    Mesh[, ,] blocks;         // Terrain data.
    Mesh[, ,] stencilBlocks;  // Foliage &ct.
    Mesh[, , ,] liquidBlocks; // Water & magma. Extra dimension is a liquid type.
    // Dirty flags for those meshes
    bool[, ,] blockDirtyBits;
    bool[, ,] liquidBlockDirtyBits;
    // Lights from magma.
    Light[, ,] magmaGlow;

    // Stuff to let the material list & various meshes & whatnot be loaded from xml specs at runtime.
    Dictionary<MatPairStruct, RemoteFortressReader.MaterialDefinition> materials;
    Dictionary<MatPairStruct, RemoteFortressReader.MaterialDefinition> items;

    // Coordinate system stuff.
    public const float tileHeight = 3.0f;
    public const float floorHeight = 0.5f;
    public const float tileWidth = 2.0f;
    public const int blockSize = 16;
    public static Vector3 DFtoUnityCoord(int x, int y, int z)
    {
        Vector3 outCoord = new Vector3(x * tileWidth, z * tileHeight, y * (-tileWidth));
        return outCoord;
    }
    public static Vector3 DFtoUnityCoord(DFCoord input)
    {
        Vector3 outCoord = new Vector3(input.x * tileWidth, input.z * tileHeight, input.y * (-tileWidth));
        return outCoord;
    }
    public static Vector3 DFtoUnityTileCenter(DFCoord input)
    {
        Vector3 result = DFtoUnityCoord(input);
        result.y += tileHeight / 2;
        return result;
    }
    public static Vector3 DFtoUnityBottomCorner(DFCoord input)
    {
        Vector3 result = DFtoUnityCoord(input);
        result.x -= tileWidth / 2;
        result.z -= tileWidth / 2;
        return result;
    }
    public static DFCoord UnityToDFCoord(Vector3 input)
    {
        int x = Mathf.RoundToInt(input.x / tileWidth);
        int y = Mathf.RoundToInt(input.z / -tileWidth);
        int z = Mathf.FloorToInt(input.y / tileHeight);
        return new DFCoord(x, y, z);
    }
    public static bool IsBlockCorner(DFCoord input)
    {
        return input.x % blockSize == 0 &&
               input.y % blockSize == 0;
    }

    // Used while meshing blocks
    MeshCombineUtility.MeshInstance[] meshBuffer;
    MeshCombineUtility.MeshInstance[] stencilMeshBuffer;

    // Used for random diagnostics
    System.Diagnostics.Stopwatch blockListTimer = new System.Diagnostics.Stopwatch();
    System.Diagnostics.Stopwatch cullTimer = new System.Diagnostics.Stopwatch();
    System.Diagnostics.Stopwatch lazyLoadTimer = new System.Diagnostics.Stopwatch();

    // Does about what you'd think it does.
    void Start()
    {
        enabled = false;

        DFConnection.RegisterConnectionCallback(this.OnConnectToDF);
    }

    void OnConnectToDF()
    {
        Debug.Log("Connected");
        enabled = true;
        mesher = BlockMesher.GetMesher(meshingThreads);
        // Initialize materials
        if (materials == null)
            materials = new Dictionary<MatPairStruct, RemoteFortressReader.MaterialDefinition>();
        materials.Clear();
        foreach (RemoteFortressReader.MaterialDefinition material in DFConnection.Instance.NetMaterialList.material_list)
        {
            materials[material.mat_pair] = material;
        }
        // Initialize items
        if (items == null)
            items = new Dictionary<MatPairStruct, RemoteFortressReader.MaterialDefinition>();
        items.Clear();
        foreach (RemoteFortressReader.MaterialDefinition material in DFConnection.Instance.NetItemList.material_list)
        {
            items[material.mat_pair] = material;
        }

        SaveTileTypeList();
        SaveMaterialList(DFConnection.Instance.NetMaterialList.material_list, "MaterialList.csv");
        SaveMaterialList(DFConnection.Instance.NetItemList.material_list, "ItemList.csv");
        SaveBuildingList();

        UpdateView();

        blockListTimer.Start();
        cullTimer.Start();
        lazyLoadTimer.Start();

        InitializeBlocks();
    }

    // Run once per frame.
    void Update()
    {
        if (!enabled) return;
        //UpdateView();
        ShowCursorInfo();
        UpdateRequestRegion();
        blockListTimer.Reset();
        blockListTimer.Start();
        //UpdateCreatures();
        UpdateBlocks();

        DrawBlocks();
    }


    void OnDestroy()
    {
        if (mesher != null)
        {
            mesher.Terminate();
            mesher = null;
        }
    }

    void UpdateView()
    {
        RemoteFortressReader.ViewInfo newView = DFConnection.Instance.PopViewInfoUpdate();
        if (newView == null) return;
        //Debug.Log("Got view");
        if (view == null || view.view_pos_z != newView.view_pos_z)
        {
            posZDirty = true;
        }
        view = newView;

        posXTile = (view.view_pos_x + (view.view_size_x / 2));
        posYTile = (view.view_pos_y + (view.view_size_y / 2));
        posZ = view.view_pos_z + 1;
    }

    // Update the region we're requesting
    void UpdateRequestRegion()
    {
        DFConnection.Instance.SetRequestRegion(
            new BlockCoord(
                PosXBlock - rangeX,
                PosYBlock - rangeY,
                posZ - rangeZdown
            ),
            new BlockCoord(
                PosXBlock + rangeX,
                PosYBlock + rangeY,
                posZ + rangeZup
            ));
    }
    void UpdateBlocks()
    {
        loadWatch.Reset();
        loadWatch.Start();
        while (true)
        {
            RemoteFortressReader.MapBlock block = DFConnection.Instance.PopMapBlockUpdate();
            if (block == null) break;
            MapDataStore.Main.StoreTiles(block);
            if (block.tiles.Count > 0)
                SetDirtyBlock(block.map_x, block.map_y, block.map_z);
            if (block.water.Count > 0 || block.magma.Count > 0)
                SetDirtyLiquidBlock(block.map_x, block.map_y, block.map_z);
        }
        loadWatch.Stop();
        genWatch.Reset();
        genWatch.Start();
        EnqueueMeshUpdates();
        genWatch.Stop();

        mesher.Poll();

        FetchNewMeshes();
    }

    void InitializeBlocks()
    {
        int blockSizeX = DFConnection.Instance.NetMapInfo.block_size_x;
        int blockSizeY = DFConnection.Instance.NetMapInfo.block_size_y;
        int blockSizeZ = DFConnection.Instance.NetMapInfo.block_size_z;

        Debug.Log("Map Size: " + blockSizeX + ", " + blockSizeY + ", " + blockSizeZ);
        blocks = new Mesh[blockSizeX * 16 / blockSize, blockSizeY * 16 / blockSize, blockSizeZ];
        stencilBlocks = new Mesh[blockSizeX * 16 / blockSize, blockSizeY * 16 / blockSize, blockSizeZ];
        liquidBlocks = new Mesh[blockSizeX * 16 / blockSize, blockSizeY * 16 / blockSize, blockSizeZ, 2];
        blockDirtyBits = new bool[blockSizeX * 16 / blockSize, blockSizeY * 16 / blockSize, blockSizeZ];
        liquidBlockDirtyBits = new bool[blockSizeX * 16 / blockSize, blockSizeY * 16 / blockSize, blockSizeZ];
        magmaGlow = new Light[blockSizeX * 16, blockSizeY * 16, blockSizeZ];
    }

    void SetDirtyBlock(int mapBlockX, int mapBlockY, int mapBlockZ)
    {
        mapBlockX = mapBlockX / blockSize;
        mapBlockY = mapBlockY / blockSize;
        blockDirtyBits[mapBlockX, mapBlockY, mapBlockZ] = true;
    }
    void SetDirtyLiquidBlock(int mapBlockX, int mapBlockY, int mapBlockZ)
    {
        mapBlockX = mapBlockX / blockSize;
        mapBlockY = mapBlockY / blockSize;
        liquidBlockDirtyBits[mapBlockX, mapBlockY, mapBlockZ] = true;
    }

    void PrintFullMaterialList()
    {
        int totalCount = DFConnection.Instance.NetMaterialList.material_list.Count;
        int limit = totalCount;
        if (limit >= 100)
            limit = 100;
        //Don't ever do this.
        for (int i = totalCount - limit; i < totalCount; i++)
        {
            //no really, don't.
            RemoteFortressReader.MaterialDefinition material = DFConnection.Instance.NetMaterialList.material_list[i];
            Debug.Log("{" + material.mat_pair.mat_index + "," + material.mat_pair.mat_type + "}, " + material.id + ", " + material.name);
        }
    }

    void SaveTileTypeList()
    {
        try
        {
            File.Delete("TiletypeList.csv");
        }
        catch (IOException)
        {
            return;
        }
        using (StreamWriter writer = new StreamWriter("TiletypeList.csv"))
        {
            foreach (Tiletype item in DFConnection.Instance.NetTiletypeList.tiletype_list)
            {
                writer.WriteLine(
                    item.name + ";" +
                    item.shape + ":" +
                    item.special + ":" +
                    item.material + ":" +
                    item.variant + ":" +
                    item.direction
                    );
            }
        }
    }
    void SaveBuildingList()
    {
        try
        {
            File.Delete("BuildingList.csv");
        }
        catch (IOException)
        {
            return;
        }
        using (StreamWriter writer = new StreamWriter("BuildingList.csv"))
        {
            foreach (var item in DFConnection.Instance.NetBuildingList.building_list)
            {
                writer.WriteLine(
                    item.name + ";" +
                    item.id + ";" +
                    item.building_type.building_type + ":" +
                    item.building_type.building_subtype + ":" +
                    item.building_type.building_custom
                    );
            }
        }
    }
    void SaveMaterialList(List<MaterialDefinition> list, string filename)
    {
        try
        {
            File.Delete(filename);
        }
        catch (IOException)
        {
            return;
        }
        using (StreamWriter writer = new StreamWriter(filename))
        {
            foreach (var item in list)
            {
                writer.WriteLine(
                    item.name + ";" +
                    item.id + ";" +
                    item.mat_pair.mat_type + ";" +
                    item.mat_pair.mat_index
                    );
            }
        }
    }

    // Have the mesher mesh all dirty tiles in the region
    void EnqueueMeshUpdates()
    {
        for (int zz = DFConnection.Instance.RequestRegionMin.z; zz < DFConnection.Instance.RequestRegionMax.z; zz++)
            for (int yy = DFConnection.Instance.RequestRegionMin.y; yy <= DFConnection.Instance.RequestRegionMax.y; yy++)
                for (int xx = DFConnection.Instance.RequestRegionMin.x; xx <= DFConnection.Instance.RequestRegionMax.x; xx++)
                {
                    if (xx < 0 || yy < 0 || zz < 0 || xx >= blocks.GetLength(0) || yy >= blocks.GetLength(1) || zz >= blocks.GetLength(2))
                    {
                        continue;
                    }
                    if (!blockDirtyBits[xx, yy, zz] && !liquidBlockDirtyBits[xx, yy, zz])
                    {
                        continue;
                    }
                    mesher.Enqueue(new DFCoord(xx * 16, yy * 16, zz), blockDirtyBits[xx, yy, zz], liquidBlockDirtyBits[xx, yy, zz]);
                    blockDirtyBits[xx, yy, zz] = false;
                    liquidBlockDirtyBits[xx, yy, zz] = false;
                }
    }

    // Get new meshes from the mesher
    void FetchNewMeshes()
    {
        while (mesher.HasNewMeshes)
        {
            posZDirty = true; //the new blocks will also need to be hidden.
            var newMeshes = mesher.Dequeue().Value;
            int block_x = newMeshes.location.x / blockSize;
            int block_y = newMeshes.location.y / blockSize;
            int block_z = newMeshes.location.z;
            if (newMeshes.tiles != null)
            {
                if (blocks[block_x, block_y, block_z] == null)
                {
                    blocks[block_x, block_y, block_z] = new Mesh();
                }
                Mesh tileMesh = blocks[block_x, block_y, block_z];
                tileMesh.Clear();
                newMeshes.tiles.CopyToMesh(tileMesh);
            }
            if (newMeshes.stencilTiles != null)
            {
                if (stencilBlocks[block_x, block_y, block_z] == null)
                {

                    stencilBlocks[block_x, block_y, block_z] = new Mesh();
                }
                Mesh stencilMesh = stencilBlocks[block_x, block_y, block_z];
                stencilMesh.Clear();
                newMeshes.stencilTiles.CopyToMesh(stencilMesh);
            }
            if (newMeshes.water != null)
            {
                if (liquidBlocks[block_x, block_y, block_z, MapDataStore.WATER_INDEX] == null)
                {
                    liquidBlocks[block_x, block_y, block_z, MapDataStore.WATER_INDEX] = new Mesh();
                }
                Mesh waterMesh = liquidBlocks[block_x, block_y, block_z, MapDataStore.WATER_INDEX];
                waterMesh.Clear();
                newMeshes.water.CopyToMesh(waterMesh);
            }
            if (newMeshes.magma != null)
            {
                if (liquidBlocks[block_x, block_y, block_z, MapDataStore.MAGMA_INDEX] == null)
                {
                    liquidBlocks[block_x, block_y, block_z, MapDataStore.MAGMA_INDEX] = new Mesh();
                }
                Mesh magmaMesh = liquidBlocks[block_x, block_y, block_z, MapDataStore.MAGMA_INDEX];
                magmaMesh.Clear();
                newMeshes.magma.CopyToMesh(magmaMesh);
            }
        }
    }


    System.Diagnostics.Stopwatch loadWatch = new System.Diagnostics.Stopwatch();
    System.Diagnostics.Stopwatch genWatch = new System.Diagnostics.Stopwatch();



    void ClearMap()
    {
        foreach (var item in blocks)
        {
            if (item != null)
                item.Clear();
        }
        foreach (var item in stencilBlocks)
        {
            if (item != null)
                item.Clear();
        }
        foreach (var item in liquidBlocks)
        {
            if (item != null)
                item.Clear();
        }
        foreach (var item in magmaGlow)
        {
            Destroy(item);
        }
        MapDataStore.Main.Reset();
    }

    void ShowCursorInfo()
    {
        cursorProperties.text = "";
        cursorProperties.text += "Cursor: ";
        cursorProperties.text += cursX + ",";
        cursorProperties.text += cursY + ",";
        cursorProperties.text += cursZ + "\n";
        var maybeTile = MapDataStore.Main[cursX, cursY, cursZ];
        if (maybeTile != null)
        {
            var tile = maybeTile.Value;
            cursorProperties.text += "Tiletype:\n";
            var tiletype = DFConnection.Instance.NetTiletypeList.tiletype_list
                [tile.tileType];
            cursorProperties.text += tiletype.name + "\n";
            cursorProperties.text +=
                tiletype.shape + ":" +
                tiletype.special + ":" +
                tiletype.material + ":" +
                tiletype.variant + ":" +
                tiletype.direction + "\n";
            var mat = tile.material;
            cursorProperties.text += "Material: ";
            cursorProperties.text += mat.mat_type + ",";
            cursorProperties.text += mat.mat_index + "\n";

            if (materials.ContainsKey(mat))
            {
                cursorProperties.text += "Material Name: ";
                cursorProperties.text += materials[mat].id + "\n";
            }
            else
                cursorProperties.text += "Unknown Material\n";

            cursorProperties.text += "\n";

            var basemat = tile.base_material;
            cursorProperties.text += "Base Material: ";
            cursorProperties.text += basemat.mat_type + ",";
            cursorProperties.text += basemat.mat_index + "\n";

            if (materials.ContainsKey(basemat))
            {
                cursorProperties.text += "Base Material Name: ";
                cursorProperties.text += materials[basemat].id + "\n";
            }
            else
                cursorProperties.text += "Unknown Base Material\n";

            cursorProperties.text += "\n";

            var layermat = tile.layer_material;
            cursorProperties.text += "Layer Material: ";
            cursorProperties.text += layermat.mat_type + ",";
            cursorProperties.text += layermat.mat_index + "\n";

            if (materials.ContainsKey(layermat))
            {
                cursorProperties.text += "Layer Material Name: ";
                cursorProperties.text += materials[layermat].id + "\n";
            }
            else
                cursorProperties.text += "Unknown Layer Material\n";

            cursorProperties.text += "\n";

            var veinmat = tile.vein_material;
            cursorProperties.text += "Vein Material: ";
            cursorProperties.text += veinmat.mat_type + ",";
            cursorProperties.text += veinmat.mat_index + "\n";

            if (materials.ContainsKey(veinmat))
            {
                cursorProperties.text += "Vein Material Name: ";
                cursorProperties.text += materials[veinmat].id + "\n";
            }
            else
                cursorProperties.text += "Unknown Vein Material\n";

            cursorProperties.text += "\n";

            var cons = tile.construction_item;
            cursorProperties.text += "Construction Item: ";
            cursorProperties.text += cons.mat_type + ",";
            cursorProperties.text += cons.mat_index + "\n";

            if (materials.ContainsKey(cons))
            {
                cursorProperties.text += "Construction Item Name: ";
                cursorProperties.text += items[cons].id + "\n";
            }
            else
                cursorProperties.text += "Unknown Construction Item\n";
        }
    }

    Dictionary<int, GameObject> creatureList;
    public GameObject creatureTemplate;

    void UpdateCreatures()
    {
        RemoteFortressReader.UnitList unitList = DFConnection.Instance.PopUnitListUpdate();
        if (unitList == null) return;
        foreach (var unit in unitList.creature_list)
        {
            if (creatureList == null)
                creatureList = new Dictionary<int, GameObject>();
            if (!creatureList.ContainsKey(unit.id))
            {
                creatureList[unit.id] = Instantiate(creatureTemplate) as GameObject;
                creatureList[unit.id].GetComponent<LayeredSprite>().Do_Sprite = true;
                creatureList[unit.id].transform.parent = gameObject.transform;
            }
            creatureList[unit.id].transform.position = DFtoUnityCoord(unit.pos_x, unit.pos_y, unit.pos_z) + new Vector3(0, 2, 0);
        }
    }

    public void UpdateCenter(Vector3 pos)
    {
        DFCoord dfPos = UnityToDFCoord(pos);
        posXTile = dfPos.x;
        posYTile = dfPos.y;
        if(posZ != dfPos.z+1)
        {
            posZ = dfPos.z+1;
            posZDirty = true;
        }
    }

    private void DrawBlocks()
    {
        for (int z = posZ - cameraViewDist; z <= posZ; z++)
            for (int x = 0; x < blocks.GetLength(0); x++)
                for (int y = 0; y < blocks.GetLength(1); y++)
                {
                    if (blocks[x, y, z] != null && blocks[x, y, z].vertexCount > 0)
                        Graphics.DrawMesh(blocks[x, y, z], Matrix4x4.identity, basicTerrainMaterial, 0);

                    if (stencilBlocks[x, y, z] != null && stencilBlocks[x, y, z].vertexCount > 0)
                        Graphics.DrawMesh(stencilBlocks[x, y, z], Matrix4x4.identity, stencilTerrainMaterial, 1);

                    if (liquidBlocks[x, y, z, MapDataStore.WATER_INDEX] != null && liquidBlocks[x, y, z, MapDataStore.WATER_INDEX].vertexCount > 0)
                        Graphics.DrawMesh(liquidBlocks[x, y, z, MapDataStore.WATER_INDEX], Matrix4x4.identity, waterMaterial, 4);

                    if (liquidBlocks[x, y, z, MapDataStore.MAGMA_INDEX] != null && liquidBlocks[x, y, z, MapDataStore.MAGMA_INDEX].vertexCount > 0)
                        Graphics.DrawMesh(liquidBlocks[x, y, z, MapDataStore.MAGMA_INDEX], Matrix4x4.identity, magmaMaterial, 4);
                }
        for (int z = posZ + 1; z <= posZ+cameraViewDist; z++)
            for (int x = 0; x < blocks.GetLength(0); x++)
                for (int y = 0; y < blocks.GetLength(1); y++)
                {
                    if (blocks[x, y, z] != null && blocks[x, y, z].vertexCount > 0)
                        Graphics.DrawMesh(blocks[x, y, z], Matrix4x4.identity, invisibleMaterial, 0);

                    if (stencilBlocks[x, y, z] != null && stencilBlocks[x, y, z].vertexCount > 0)
                        Graphics.DrawMesh(stencilBlocks[x, y, z], Matrix4x4.identity, invisibleStencilMaterial, 1);

                    //if (liquidBlocks[x, y, z, MapDataStore.WATER_INDEX] != null && liquidBlocks[x, y, z, MapDataStore.WATER_INDEX].vertexCount > 0)
                    //    Graphics.DrawMesh(liquidBlocks[x, y, z, MapDataStore.WATER_INDEX], Matrix4x4.identity, waterMaterial, 4);

                    //if (liquidBlocks[x, y, z, MapDataStore.MAGMA_INDEX] != null && liquidBlocks[x, y, z, MapDataStore.MAGMA_INDEX].vertexCount > 0)
                    //    Graphics.DrawMesh(liquidBlocks[x, y, z, MapDataStore.MAGMA_INDEX], Matrix4x4.identity, magmaMaterial, 4);
                }
    }

}
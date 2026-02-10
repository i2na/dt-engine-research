import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { CacheManager } from "../core/CacheSystem";

/**
 * GLB Loader with GUID extraction from EXT_structural_metadata
 */
export class DTGLBLoader {
    private loader = new GLTFLoader();
    private guidToMesh = new Map<string, THREE.Object3D>();
    private meshToGuid = new Map<THREE.Object3D, string>();
    private cacheManager: CacheManager;

    constructor(cacheManager: CacheManager) {
        this.cacheManager = cacheManager;
    }

    async load(url: string): Promise<THREE.Group> {
        console.log("Loading GLB:", url);

        return new Promise((resolve, reject) => {
            this.loader.load(
                url,
                (gltf) => {
                    console.log("✓ GLB loaded successfully");

                    // Extract GUID metadata from node extras
                    this.extractGuidMetadata(gltf.scene);

                    // Setup materials and shadows
                    this.setupScene(gltf.scene);

                    resolve(gltf.scene);
                },
                (progress) => {
                    const percent = (progress.loaded / progress.total) * 100;
                    console.log(`Loading: ${percent.toFixed(1)}%`);
                },
                (error) => {
                    console.error("Failed to load GLB:", error);
                    reject(error);
                }
            );
        });
    }

    private extractGuidMetadata(scene: THREE.Group): void {
        let guidCount = 0;

        scene.traverse((node) => {
            const userData = (node as any).userData;
            const guid = userData?.guid || userData?.UniqueId;

            if (guid) {
                const name = userData?.name || node.name || "Unknown";
                this.guidToMesh.set(guid, node);
                this.meshToGuid.set(node, guid);

                node.traverse((child) => {
                    if (child !== node) {
                        this.meshToGuid.set(child, guid);
                    }
                });

                this.cacheManager.preloadInline(guid, {
                    guid: guid,
                    category: this.extractCategory(name),
                });
                guidCount++;
            }

            if (node instanceof THREE.Mesh) {
                node.castShadow = true;
                node.receiveShadow = true;
            }
        });

        console.log(`✓ Extracted ${guidCount} GUID mappings`);
    }

    private setupScene(scene: THREE.Group): void {
        scene.traverse((node) => {
            if (!(node instanceof THREE.Mesh)) return;

            const geometry = node.geometry as THREE.BufferGeometry;
            if (geometry && !geometry.attributes.normal) {
                geometry.computeVertexNormals();
            }

            if (node.material) {
                const materials = Array.isArray(node.material) ? node.material : [node.material];
                materials.forEach((mat) => {
                    mat.side = THREE.DoubleSide;
                    if (mat instanceof THREE.MeshStandardMaterial) {
                        mat.roughness = 0.8;
                        mat.metalness = 0.1;
                    }
                });
            } else {
                node.material = new THREE.MeshStandardMaterial({
                    color: 0xd8d8d8,
                    roughness: 0.8,
                    metalness: 0.1,
                    side: THREE.DoubleSide,
                });
            }

            const edges = new THREE.EdgesGeometry(geometry, 30);
            const line = new THREE.LineSegments(
                edges,
                new THREE.LineBasicMaterial({ color: 0x333333 })
            );
            line.raycast = () => {};
            line.renderOrder = 1;
            node.add(line);
        });
    }

    private extractCategory(name: string): string {
        // Simple category extraction from name
        // In production, this would be more sophisticated
        if (name.includes("Wall")) return "Walls";
        if (name.includes("Floor")) return "Floors";
        if (name.includes("Door")) return "Doors";
        if (name.includes("Window")) return "Windows";
        if (name.includes("Column")) return "Structural Columns";
        return "Unknown";
    }

    getMeshByGuid(guid: string): THREE.Object3D | undefined {
        return this.guidToMesh.get(guid);
    }

    getGuidByMesh(mesh: THREE.Object3D): string | undefined {
        return this.meshToGuid.get(mesh);
    }

    getAllGuids(): string[] {
        return Array.from(this.guidToMesh.keys());
    }

    getStats() {
        return {
            totalElements: this.guidToMesh.size,
        };
    }
}

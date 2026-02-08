import * as THREE from "three";

/**
 * GUID-based object picking via raycasting
 * Resolves 2D screen coordinates to 3D BIM elements
 */
export class GuidResolver {
    private raycaster = new THREE.Raycaster();
    private mouse = new THREE.Vector2();
    private guidToMesh: Map<string, THREE.Object3D>;
    private meshToGuid: Map<THREE.Object3D, string>;

    constructor(guidToMesh: Map<string, THREE.Object3D>, meshToGuid: Map<THREE.Object3D, string>) {
        this.guidToMesh = guidToMesh;
        this.meshToGuid = meshToGuid;
    }

    /**
     * Pick object at screen coordinates
     * Returns GUID if hit, null otherwise
     */
    pick(x: number, y: number, camera: THREE.Camera, scene: THREE.Scene): string | null {
        // Normalize mouse coordinates to [-1, 1]
        this.mouse.x = (x / window.innerWidth) * 2 - 1;
        this.mouse.y = -(y / window.innerHeight) * 2 + 1;

        // Update raycaster
        this.raycaster.setFromCamera(this.mouse, camera);

        // Intersect with scene
        const intersects = this.raycaster.intersectObjects(scene.children, true);

        if (intersects.length > 0) {
            const firstHit = intersects[0];
            const hitObject = firstHit.object;

            // Traverse up to find object with GUID
            let current: THREE.Object3D | null = hitObject;
            while (current) {
                const guid = this.meshToGuid.get(current);
                if (guid) {
                    return guid;
                }
                current = current.parent;
            }
        }

        return null;
    }

    /**
     * Highlight object by GUID
     */
    highlightByGuid(guid: string, color: number = 0x4a9eff): void {
        const mesh = this.guidToMesh.get(guid);
        if (mesh && mesh instanceof THREE.Mesh) {
            // Store original material
            if (!(mesh as any)._originalMaterial) {
                (mesh as any)._originalMaterial = mesh.material;
            }

            // Apply highlight material
            const highlightMaterial = new THREE.MeshStandardMaterial({
                color: color,
                emissive: color,
                emissiveIntensity: 0.3,
                metalness: 0.2,
                roughness: 0.6,
            });

            mesh.material = highlightMaterial;
        }
    }

    /**
     * Clear all highlights
     */
    clearHighlight(guid?: string): void {
        if (guid) {
            const mesh = this.guidToMesh.get(guid);
            if (mesh && mesh instanceof THREE.Mesh) {
                if ((mesh as any)._originalMaterial) {
                    mesh.material = (mesh as any)._originalMaterial;
                    delete (mesh as any)._originalMaterial;
                }
            }
        } else {
            // Clear all highlights
            this.meshToGuid.forEach((guid, mesh) => {
                if (mesh instanceof THREE.Mesh && (mesh as any)._originalMaterial) {
                    mesh.material = (mesh as any)._originalMaterial;
                    delete (mesh as any)._originalMaterial;
                }
            });
        }
    }
}

import * as THREE from "three";

export class GuidResolver {
    private raycaster = new THREE.Raycaster();
    private mouse = new THREE.Vector2();
    private guidToMesh: Map<string, THREE.Object3D>;
    private meshToGuid: Map<THREE.Object3D, string>;

    constructor(guidToMesh: Map<string, THREE.Object3D>, meshToGuid: Map<THREE.Object3D, string>) {
        this.guidToMesh = guidToMesh;
        this.meshToGuid = meshToGuid;
    }

    pick(ndcX: number, ndcY: number, camera: THREE.Camera, scene: THREE.Scene): string | null {
        this.mouse.x = ndcX;
        this.mouse.y = ndcY;
        this.raycaster.setFromCamera(this.mouse, camera);

        const intersects = this.raycaster.intersectObjects(scene.children, true);

        if (intersects.length > 0) {
            const hitObject = intersects[0].object;
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

    highlightByGuid(guid: string, color: number = 0x4a9eff): void {
        const object = this.guidToMesh.get(guid);
        if (!object) return;

        const highlightMaterial = new THREE.MeshStandardMaterial({
            color: color,
            emissive: color,
            emissiveIntensity: 0.25,
            metalness: 0.2,
            roughness: 0.6,
        });

        object.traverse((node) => {
            if (node instanceof THREE.Mesh) {
                if (!(node as any)._originalMaterial) {
                    (node as any)._originalMaterial = node.material;
                }
                node.material = highlightMaterial;
            }
        });
    }

    clearHighlight(guid?: string): void {
        const clearOne = (obj: THREE.Object3D) => {
            obj.traverse((node) => {
                if (node instanceof THREE.Mesh && (node as any)._originalMaterial) {
                    node.material = (node as any)._originalMaterial;
                    delete (node as any)._originalMaterial;
                }
            });
        };
        if (guid) {
            const object = this.guidToMesh.get(guid);
            if (object) clearOne(object);
        } else {
            this.meshToGuid.forEach((_, obj) => clearOne(obj));
        }
    }
}

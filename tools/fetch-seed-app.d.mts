export interface SeedRelease {
  version: string;
  hostApiVersion: string;
  minWorkbenchVersion: string;
  url: string;
  size: number;
  sha256: string;
  signature: { keyId: string; signature: string };
}

export function validateSeedRelease(release: unknown): SeedRelease;
export function verifySeedPayload(payload: Uint8Array, release: SeedRelease, trustedKeys: Record<string, string>): void;
export function fetchSeedApp(options?: { fetchImpl?: typeof fetch; outputDir?: string; trustedKeys?: Record<string, string> }): Promise<SeedRelease>;

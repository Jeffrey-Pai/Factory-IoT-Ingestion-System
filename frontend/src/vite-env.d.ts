/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Optional absolute base URL for the backend API. Empty = same-origin. */
  readonly VITE_API_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

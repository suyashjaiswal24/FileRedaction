export interface BoundingRegion {
  pageNumber: number;
  polygon: number[];
}

export interface PiiEntity {
  id: string;
  text: string;
  category: string;
  subCategory: string;
  confidenceScore: number;
  occurrenceCount: number;
  boundingRegions: BoundingRegion[];
}

// Returned immediately from POST /upload
export interface UploadAcceptedResponse {
  sessionId: string;
  originalFileName: string;
  status: 'processing';
}

// Returned by GET /{sessionId}/status
export interface UploadStatusResponse {
  status: 'processing' | 'ready' | 'error';
  phase: 'extracting' | 'detecting' | '';
  errorMessage?: string;
  entities?: PiiEntity[];
  originalFileName?: string;
}

// Full result once ready — used internally after polling completes
export interface UploadResponse {
  sessionId: string;
  originalFileName: string;
  entities: PiiEntity[];
}

export interface PreviewResponse {
  fileUrl: string;
  hasHighlights: boolean;
  fileType: string;
}

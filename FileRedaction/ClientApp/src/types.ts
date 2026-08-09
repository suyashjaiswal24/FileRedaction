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
  source?: string;
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
  phase: 'extracting' | 'detecting' | 'extracting_with_faces' | '';
  errorMessage?: string;
  entities?: PiiEntity[];
  originalFileName?: string;
  isEmail?: boolean;
  attachmentCount?: number;
}

// Full result once ready — used internally after polling completes
export interface UploadResponse {
  sessionId: string;
  originalFileName: string;
  entities: PiiEntity[];
  isEmail?: boolean;
  attachmentCount?: number;
}

export interface PreviewResponse {
  fileUrl: string;
  hasHighlights: boolean;
  fileType: string;
  isEmailSession?: boolean;
  attachmentCount?: number;
}

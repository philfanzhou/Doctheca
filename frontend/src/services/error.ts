const PROBLEM_JSON_MEDIA_TYPE = 'application/problem+json'

interface ResponseLike {
  data?: unknown
  headers?: unknown
}

function readContentType(response: ResponseLike | undefined): string {
  const headers = response?.headers
  if (!headers || typeof headers !== 'object') {
    return ''
  }

  // axios v1 exposes response headers as an AxiosHeaders instance (with a get() accessor);
  // plain header objects are supported through the same lowercase key lookup.
  const get = (headers as { get?: unknown }).get
  if (typeof get === 'function') {
    const value = (get as (name: string) => unknown).call(headers, 'content-type')
    if (typeof value === 'string') {
      return value.toLowerCase()
    }
  }

  const direct = (headers as Record<string, unknown>)['content-type']
  return typeof direct === 'string' ? direct.toLowerCase() : ''
}

/** True only when the response content type is application/problem+json. */
export function isProblemDetailsResponse(response: ResponseLike | undefined): boolean {
  return readContentType(response).includes(PROBLEM_JSON_MEDIA_TYPE)
}

/**
 * Extracts the fixed, safe `title` of an application/problem+json response (the ServiceMantle
 * problem-details boundary on the marked JSON admin endpoints). Returns null for any other
 * content type; `detail` is never read and the raw JSON is never surfaced.
 */
export function getProblemDetailsTitle(response: ResponseLike | undefined): string | null {
  if (!isProblemDetailsResponse(response)) {
    return null
  }

  const data = response?.data
  const title = data && typeof data === 'object' ? (data as { title?: unknown }).title : undefined
  return typeof title === 'string' && title.length > 0 ? title : null
}

export function getDocErrorMessage(error: unknown): string {
  if (error && typeof error === 'object' && 'isAxiosError' in error) {
    const axiosError = error as unknown as { response?: ResponseLike; message: string }
    const data = axiosError.response?.data as { message?: string } | undefined
    // Priority: business `message` → fixed problem+json `title` → axios message → fallback.
    if (data?.message) {
      return data.message
    }
    const problemTitle = getProblemDetailsTitle(axiosError.response)
    if (problemTitle) {
      return problemTitle
    }
    return axiosError.message
  }

  if (error instanceof Error) {
    return error.message
  }

  return 'An unknown error occurred'
}

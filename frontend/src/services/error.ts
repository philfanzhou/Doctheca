export function getDocErrorMessage(error: unknown): string {
  if (error && typeof error === 'object' && 'isAxiosError' in error) {
    const axiosError = error as unknown as { response?: { data?: { message?: string } }; message: string }
    const data = axiosError.response?.data as { message?: string } | undefined
    if (data?.message) {
      return data.message
    }
    return axiosError.message
  }

  if (error instanceof Error) {
    return error.message
  }

  return 'An unknown error occurred'
}

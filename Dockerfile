FROM mcr.microsoft.com/dotnet/aspnet:5.0

# Copy across folders and files into the image
COPY ./deploy .

# Start the API
EXPOSE 80
CMD dotnet ./Server.dll